using System.Text.Json;
using System.Text.Json.Nodes;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Parsing;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 输出合并问题类型。
/// </summary>
public enum OutputMergeIssueKind
{
    InvalidTranslationKey,
    InvalidRelativePath,
    MissingTranslation,
    EnglishTemplateMissing,
    TemplateJsonInvalid,
    FieldPathNotFound,
    OutputVerificationFailed,
    OutputWriteFailed,
}

/// <summary>
/// 单条输出合并问题。
/// </summary>
public sealed class OutputMergeIssue
{
    public required OutputMergeIssueKind Kind { get; init; }
    public string? RelativeFilePath { get; init; }
    public string? TranslationKey { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// 输出合并报告。
/// </summary>
public sealed class OutputMergeResult
{
    /// <summary>本轮预期写入的文件数</summary>
    public int RequestedFileCount { get; init; }

    /// <summary>本轮预期写入的条目数</summary>
    public int RequestedEntryCount { get; init; }

    /// <summary>实际写入的条目数</summary>
    public int WrittenEntryCount { get; init; }

    /// <summary>写后校验通过的条目数</summary>
    public int VerifiedEntryCount { get; init; }

    /// <summary>已成功写入且通过校验的相对文件路径</summary>
    public required IReadOnlyList<string> Files { get; init; }

    /// <summary>
    /// 实际写入 output 的 UnitKey 集合（第9.0B-P4轮）：供 ReleaseGate 做
    /// Missing Expected Key / Unexpected Output Key 校验（与 Merge 同一权威 Key 语义）。
    /// </summary>
    public IReadOnlyList<string> WrittenKeys { get; init; } = Array.Empty<string>();

    /// <summary>未写入或校验失败的原因</summary>
    public required IReadOnlyList<OutputMergeIssue> Issues { get; init; }

    public int WrittenFileCount => Files.Count;

    /// <summary>是否所有预期条目均完成写入与校验</summary>
    public bool IsComplete => Issues.Count == 0;
}

/// <summary>
/// 新版中文输出合并器。
///
/// 每个文件都按“应用全部预期条目 → 写临时文件 → 写后校验 → 原子替换”的顺序执行。
/// 任一字段无法定位时，该文件不会被部分写入，避免把英文残留误当成成功输出。
/// </summary>
public sealed class MergeOutputService
{
    /// <summary>
    /// 兼容旧调用方：只返回成功写入的文件列表。
    /// 新调用方应使用 <see cref="MergeAllWithReport"/> 获取完整核验结果。
    /// </summary>
    public IReadOnlyList<string> MergeAll(
        string newEnglishRoot,
        IReadOnlyDictionary<string, string> translations,
        string outputRoot)
        => MergeAllWithReport(newEnglishRoot, translations, outputRoot).Files;

    /// <summary>
    /// 合并译文并返回逐文件、逐字段的详细报告。
    /// </summary>
    /// <param name="templateRoot">
    /// 输出模板所在的**权威源目录**（第9.0B-P4轮）：
    ///   EN_ONLY → 当前英文目录（Localize/en）；
    ///   KR_EN / KR_JP / KR_ONLY → 当前韩文目录（Localize/kr）。
    /// </param>
    /// <param name="expectedKeys">
    /// 本轮必须写入的 UnitKey 集合。传入后，某文件只要缺少其中任一译文，就不会生成该文件。
    /// 未传入时，以 translations 的键作为预期集合，保持旧行为兼容。
    /// </param>
    /// <param name="templateLanguage">模板语言（决定物理文件名前缀：EN_ / KR_）；默认英文（旧调用方兼容）。</param>
    public OutputMergeResult MergeAllWithReport(
        string templateRoot,
        IReadOnlyDictionary<string, string> translations,
        string outputRoot,
        IEnumerable<string>? expectedKeys = null,
        SourceLanguage templateLanguage = SourceLanguage.English)
    {
        var issues = new List<OutputMergeIssue>();
        var translationsByFile = new Dictionary<string, List<OutputTranslation>>(StringComparer.OrdinalIgnoreCase);
        var expectedByFile = new Dictionary<string, List<OutputTarget>>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in translations)
        {
            if (!TryParseTarget(item.Key, out var target, out var reason))
            {
                issues.Add(new OutputMergeIssue
                {
                    Kind = OutputMergeIssueKind.InvalidTranslationKey,
                    TranslationKey = item.Key,
                    Message = reason,
                });
                continue;
            }

            if (!translationsByFile.TryGetValue(target.RelativeFilePath, out var values))
            {
                values = new List<OutputTranslation>();
                translationsByFile[target.RelativeFilePath] = values;
            }

            values.Add(new OutputTranslation(target.Key, target.RelativeFilePath, target.FieldPath, item.Value));
        }

        if (expectedKeys is not null)
        {
            foreach (var key in expectedKeys.Distinct(StringComparer.Ordinal))
            {
                if (!TryParseTarget(key, out var target, out var reason))
                {
                    issues.Add(new OutputMergeIssue
                    {
                        Kind = OutputMergeIssueKind.InvalidTranslationKey,
                        TranslationKey = key,
                        Message = reason,
                    });
                    continue;
                }

                if (!expectedByFile.TryGetValue(target.RelativeFilePath, out var expected))
                {
                    expected = new List<OutputTarget>();
                    expectedByFile[target.RelativeFilePath] = expected;
                }

                expected.Add(target);
            }
        }

        // 未指定预期集合时，仍按照译文集合本身执行旧行为。
        if (expectedKeys is null)
        {
            foreach (var group in translationsByFile)
            {
                expectedByFile[group.Key] = group.Value
                    .Select(value => new OutputTarget(value.Key, value.RelativeFilePath, value.FieldPath))
                    .ToList();
            }
        }

        var allRelativePaths = expectedByFile.Keys
            .Concat(translationsByFile.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var outputFiles = new List<string>();
        var writtenKeys = new List<string>();
        var writtenEntries = 0;
        var verifiedEntries = 0;

        foreach (var relativeFilePath in allRelativePaths)
        {
            var expected = expectedByFile.TryGetValue(relativeFilePath, out var expectedTargets)
                ? expectedTargets
                : new List<OutputTarget>();
            var values = translationsByFile.TryGetValue(relativeFilePath, out var translatedValues)
                ? translatedValues
                : new List<OutputTranslation>();
            var valueByKey = values.ToDictionary(value => value.Key, StringComparer.Ordinal);

            // 传入预期条目时，缺译条目不再拦下整个文件（见下方第9.0C.5轮说明）。
            var missingTargets = expected
                .Where(target => !valueByKey.ContainsKey(target.Key))
                .ToList();

            // 第9.0C.5轮（产品决策：尽量多写 + 标记待审）：缺译条目不再「跳过整个文件」，
            // 而是保留权威源原文写入，并逐条记录 MissingTranslation 供人工审核。
            if (missingTargets.Count > 0)
            {
                foreach (var missing in missingTargets)
                {
                    issues.Add(new OutputMergeIssue
                    {
                        Kind = OutputMergeIssueKind.MissingTranslation,
                        RelativeFilePath = relativeFilePath,
                        TranslationKey = missing.Key,
                        Message = "本轮没有该字段的译文：已保留权威源原文写入，并标记为待人工审核。",
                    });
                }
            }

            if (values.Count == 0 && expected.Count == 0)
            {
                continue;
            }

            var templateRelative = LanguageFileMapper.ToPhysicalRelativePath(templateLanguage, relativeFilePath);
            if (!TryResolvePathUnderRoot(templateRoot, templateRelative, out var templatePath)
                || !File.Exists(templatePath))
            {
                issues.Add(new OutputMergeIssue
                {
                    Kind = OutputMergeIssueKind.EnglishTemplateMissing,
                    RelativeFilePath = relativeFilePath,
                    Message = $"找不到对应模板: {templateRelative}（权威源: {SourceLanguageHelper.ToCode(templateLanguage)}）",
                });
                continue;
            }

            if (!TryResolvePathUnderRoot(outputRoot, relativeFilePath, out var outputPath))
            {
                issues.Add(new OutputMergeIssue
                {
                    Kind = OutputMergeIssueKind.InvalidRelativePath,
                    RelativeFilePath = relativeFilePath,
                    Message = "输出相对路径无效，已拒绝写入。",
                });
                continue;
            }

            var fileResult = MergeSingleFile(templatePath, outputPath, values);
            issues.AddRange(fileResult.Issues);
            if (!fileResult.WasWritten)
            {
                continue;
            }

            outputFiles.Add(relativeFilePath);
            writtenEntries += fileResult.WrittenEntryCount;
            verifiedEntries += fileResult.VerifiedEntryCount;
            // 第9.0C.5轮：缺译条目已用权威源原文写入该文件 ⇒ 它们同样属于"已写入的 Key"
            //（ReleaseGate 因此不会把它们误判为结构缺失，而会以 UNTRANSLATED_ENTRY 提示待审）。
            writtenKeys.AddRange(expected.Count > 0
                ? expected.Select(target => target.Key)
                : values.Select(value => value.Key));
        }

        return new OutputMergeResult
        {
            RequestedFileCount = allRelativePaths.Count,
            RequestedEntryCount = expectedByFile.Values.Sum(items => items.Count),
            WrittenEntryCount = writtenEntries,
            VerifiedEntryCount = verifiedEntries,
            Files = outputFiles,
            WrittenKeys = writtenKeys,
            Issues = issues,
        };
    }

    private static FileMergeResult MergeSingleFile(
        string templatePath,
        string outputPath,
        IReadOnlyList<OutputTranslation> entries)
    {
        var issues = new List<OutputMergeIssue>();
        JsonNode? root;
        string originalText;
        byte[] rawBytes;

        try
        {
            rawBytes = File.ReadAllBytes(templatePath);
            var hasBom = HasUtf8Bom(rawBytes);
            originalText = System.Text.Encoding.UTF8.GetString(hasBom ? rawBytes[3..] : rawBytes);
            root = JsonNode.Parse(originalText);
            if (root is null)
            {
                throw new JsonException("输出模板根节点为空。");
            }
        }
        catch (Exception ex)
        {
            issues.Add(new OutputMergeIssue
            {
                Kind = OutputMergeIssueKind.TemplateJsonInvalid,
                RelativeFilePath = Path.GetFileName(outputPath),
                Message = $"输出模板无法解析: {ex.Message}",
            });
            return new FileMergeResult(false, 0, 0, issues);
        }

        foreach (var entry in entries)
        {
            if (!TryApplyValue(root, entry.FieldPath, entry.Value, out var reason))
            {
                issues.Add(new OutputMergeIssue
                {
                    Kind = OutputMergeIssueKind.FieldPathNotFound,
                    RelativeFilePath = entry.RelativeFilePath,
                    TranslationKey = entry.Key,
                    Message = reason,
                });
            }
        }

        // 同一文件只要有一个字段未命中，就不生成部分输出。
        if (issues.Count > 0)
        {
            return new FileMergeResult(false, 0, 0, issues);
        }

        var newline = DetectNewline(rawBytes);
        var hasBomForOutput = HasUtf8Bom(rawBytes);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            IndentCharacter = ' ',
            IndentSize = DetectIndentSize(originalText),
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var resultText = root.ToJsonString(options).Replace("\r\n", "\n");
        if (newline == "\r\n")
        {
            resultText = resultText.Replace("\n", "\r\n");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, resultText, hasBomForOutput
                ? new System.Text.UTF8Encoding(true)
                : new System.Text.UTF8Encoding(false));

            JsonNode? verificationRoot = JsonNode.Parse(File.ReadAllText(tempPath).TrimStart('\uFEFF'));
            if (verificationRoot is null)
            {
                throw new JsonException("写入后的输出根节点为空。");
            }

            var verified = 0;
            foreach (var entry in entries)
            {
                if (!TryReadStringValue(verificationRoot, entry.FieldPath, out var actualValue, out var reason)
                    || !string.Equals(actualValue, entry.Value, StringComparison.Ordinal))
                {
                    issues.Add(new OutputMergeIssue
                    {
                        Kind = OutputMergeIssueKind.OutputVerificationFailed,
                        RelativeFilePath = entry.RelativeFilePath,
                        TranslationKey = entry.Key,
                        Message = string.IsNullOrWhiteSpace(reason)
                            ? "写后校验发现译文与预期不一致。"
                            : reason,
                    });
                    continue;
                }

                verified++;
            }

            if (issues.Count > 0)
            {
                return new FileMergeResult(false, 0, 0, issues);
            }

            File.Move(tempPath, outputPath, overwrite: true);
            return new FileMergeResult(true, entries.Count, verified, issues);
        }
        catch (Exception ex)
        {
            issues.Add(new OutputMergeIssue
            {
                Kind = OutputMergeIssueKind.OutputWriteFailed,
                RelativeFilePath = entries[0].RelativeFilePath,
                Message = $"输出写入或校验失败: {ex.Message}",
            });
            return new FileMergeResult(false, 0, 0, issues);
        }
        finally
        {
            // 仅删除当前调用创建、且尚未原子替换成功的临时文件。
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool TryParseTarget(string key, out OutputTarget target, out string reason)
    {
        target = default;
        reason = string.Empty;
        var parts = key.Split('|', 3, StringSplitOptions.None);
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[2]))
        {
            reason = "译文键格式无效，应为 RelativeFilePath|RecordId|FieldPath。";
            return false;
        }

        if (!TryNormalizeRelativePath(parts[0], out var relativeFilePath))
        {
            reason = "译文键中的相对文件路径无效。";
            return false;
        }

        target = new OutputTarget(key, relativeFilePath, parts[2]);
        return true;
    }

    private static bool TryResolvePathUnderRoot(string root, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (!TryNormalizeRelativePath(relativePath, out var normalized))
        {
            return false;
        }

        var rootFullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = Path.GetFullPath(Path.Combine(rootFullPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var allowedPrefix = rootFullPath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private static bool TryNormalizeRelativePath(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
        {
            return false;
        }

        var parts = value.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or ".."))
        {
            return false;
        }

        normalized = string.Join('/', parts);
        return true;
    }

    private static bool TryApplyValue(JsonNode root, string fieldPath, string value, out string reason)
    {
        if (!TryParsePathSegments(fieldPath, out var segments, out reason))
        {
            return false;
        }

        JsonNode? current = root;
        for (var index = 0; index < segments.Count - 1; index++)
        {
            var segment = segments[index];
            if (segment.IsArrayIndex)
            {
                if (current is not JsonArray array || segment.Index < 0 || segment.Index >= array.Count)
                {
                    reason = $"字段路径数组下标无效: {fieldPath}";
                    return false;
                }

                current = array[segment.Index];
            }
            else
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment.Name, out current) || current is null)
                {
                    reason = $"字段路径不存在: {fieldPath}";
                    return false;
                }
            }
        }

        var last = segments[^1];
        if (last.IsArrayIndex)
        {
            if (current is not JsonArray array || last.Index < 0 || last.Index >= array.Count)
            {
                reason = $"字段路径数组下标无效: {fieldPath}";
                return false;
            }

            array[last.Index] = JsonValue.Create(value);
            reason = string.Empty;
            return true;
        }

        if (current is not JsonObject lastObject || !lastObject.ContainsKey(last.Name))
        {
            reason = $"字段路径不存在: {fieldPath}";
            return false;
        }

        lastObject[last.Name] = JsonValue.Create(value);
        reason = string.Empty;
        return true;
    }

    private static bool TryReadStringValue(JsonNode root, string fieldPath, out string actualValue, out string reason)
    {
        actualValue = string.Empty;
        if (!TryParsePathSegments(fieldPath, out var segments, out reason))
        {
            return false;
        }

        JsonNode? current = root;
        foreach (var segment in segments)
        {
            if (segment.IsArrayIndex)
            {
                if (current is not JsonArray array || segment.Index < 0 || segment.Index >= array.Count)
                {
                    reason = $"写后校验时数组下标无效: {fieldPath}";
                    return false;
                }

                current = array[segment.Index];
            }
            else
            {
                if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment.Name, out current) || current is null)
                {
                    reason = $"写后校验时字段不存在: {fieldPath}";
                    return false;
                }
            }
        }

        if (current is JsonValue value && value.TryGetValue<string>(out var text))
        {
            actualValue = text;
            reason = string.Empty;
            return true;
        }

        reason = $"写后校验时字段不是字符串: {fieldPath}";
        return false;
    }

    private static bool TryParsePathSegments(string fieldPath, out List<PathSegment> segments, out string reason)
    {
        segments = new List<PathSegment>();
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            reason = "字段路径为空。";
            return false;
        }

        foreach (var token in fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bracket = token.IndexOf('[');
            if (bracket < 0)
            {
                segments.Add(new PathSegment(token, false, -1));
                continue;
            }

            if (bracket == 0 || !token.EndsWith(']') || token.IndexOf('[', bracket + 1) >= 0)
            {
                reason = $"字段路径格式无效: {fieldPath}";
                return false;
            }

            var name = token[..bracket];
            var indexText = token[(bracket + 1)..^1];
            if (!int.TryParse(indexText, out var arrayIndex) || arrayIndex < 0)
            {
                reason = $"字段路径数组下标无效: {fieldPath}";
                return false;
            }

            segments.Add(new PathSegment(name, false, -1));
            segments.Add(new PathSegment(string.Empty, true, arrayIndex));
        }

        if (segments.Count == 0)
        {
            reason = "字段路径为空。";
            return false;
        }

        return true;
    }

    private static bool HasUtf8Bom(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static string DetectNewline(byte[] bytes)
    {
        for (var index = 0; index < bytes.Length - 1; index++)
        {
            if (bytes[index] == 0x0D && bytes[index + 1] == 0x0A)
            {
                return "\r\n";
            }

            if (bytes[index] == 0x0A)
            {
                return "\n";
            }
        }

        return Environment.NewLine;
    }

    private static int DetectIndentSize(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith(' ') && !line.TrimStart().StartsWith('{'))
            {
                var indent = line.Length - line.TrimStart().Length;
                if (indent > 0 && indent <= 8)
                {
                    return indent;
                }
            }
        }

        return 2;
    }

    private readonly record struct OutputTarget(string Key, string RelativeFilePath, string FieldPath);

    private readonly record struct OutputTranslation(string Key, string RelativeFilePath, string FieldPath, string Value);

    private readonly record struct PathSegment(string Name, bool IsArrayIndex, int Index);

    private readonly record struct FileMergeResult(
        bool WasWritten,
        int WrittenEntryCount,
        int VerifiedEntryCount,
        IReadOnlyList<OutputMergeIssue> Issues);
}
