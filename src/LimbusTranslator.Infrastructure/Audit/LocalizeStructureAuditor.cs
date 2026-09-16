using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Parsing;

namespace LimbusTranslator.Infrastructure.Audit;

/// <summary>
/// 多语言 JSON 结构差异**只读**审计器（第9.0B-P7轮）。
///
/// 目的：用真实游戏数据回答「KR / EN / JP 同名文件内部的结构差异有多大」，
/// 从而判断 P2-δ（Merge 依赖 FieldPath 写回）在真实数据上是否成立。
///
/// 安全保证：
///   - 只做 Enumerate / Read / Parse，**绝不写入、移动、删除、重命名任何文件**；
///   - 不调用任何翻译或网络接口；
///   - 解析失败只记录为 Class 5，不让整个审计崩溃。
///
/// 分类复用项目自己的 Parser / TranslationUnit / UnitKey（而不是 raw JSON diff），
/// 因为真正影响生产系统的是「项目认为的 UnitKey / FieldPath」。
/// </summary>
public static class LocalizeStructureAuditor
{
    /// <summary>每个分级最多保留的示例数。</summary>
    public const int MaxExamplesPerClass = 20;

    /// <summary>执行审计（纯只读）。</summary>
    public static LocalizeStructureAuditReport Run(LocalizeStructureAuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var notes = new List<string>();
        var files = new Dictionary<SourceLanguage, List<(string Logical, string Full)>>();
        foreach (var language in SourceLanguageHelper.All)
        {
            files[language] = Scan(language, options.LocalizeRoot, notes);
        }

        var kr = files[SourceLanguage.Korean];
        var en = files[SourceLanguage.English];
        var jp = files[SourceLanguage.Japanese];
        var krNames = kr.Select(f => f.Logical).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enNames = en.Select(f => f.Logical).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var jpNames = jp.Select(f => f.Logical).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var krOnly = krNames.Except(enNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var enOnly = enNames.Except(krNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var jpOnly = jpNames.Except(krNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var commonKrEn = krNames.Intersect(enNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var commonKrJp = krNames.Intersect(jpNames, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        var commonAll = krNames.Intersect(enNames, StringComparer.OrdinalIgnoreCase).Intersect(jpNames, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var parser = new JsonGameFileParser(options.ConfigDir);
        var cache = new Dictionary<string, ParsedFile>(StringComparer.OrdinalIgnoreCase);

        ParsedFile Parse(SourceLanguage language, string logical)
        {
            var cacheKey = $"{SourceLanguageHelper.ToCode(language)}:{logical}";
            if (cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var entry = files[language].FirstOrDefault(f => string.Equals(f.Logical, logical, StringComparison.OrdinalIgnoreCase));
            var parsed = entry.Full is null
                ? new ParsedFile { LogicalFile = logical, Language = language, ParseFailed = true, Error = "文件不存在" }
                : AnalyzeFile(parser, language, logical, entry.Full);
            cache[cacheKey] = parsed;
            return parsed;
        }

        var krEnDiffs = commonKrEn
            .Select(logical => Classify(Parse(SourceLanguage.Korean, logical), Parse(SourceLanguage.English, logical), options.MaxExamplesPerClass))
            .ToList();
        var krJpDiffs = commonKrJp
            .Select(logical => Classify(Parse(SourceLanguage.Korean, logical), Parse(SourceLanguage.Japanese, logical), options.MaxExamplesPerClass))
            .ToList();

        var allDiffs = krEnDiffs.Concat(krJpDiffs).ToList();
        var parserUnsupported = allDiffs
            .Where(diff => diff.Class == LocalizeStructureClass.Class5ParserUnsupported)
            .Select(diff => diff.LogicalFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var rootKindMismatch = allDiffs
            .Where(diff => diff.Class == LocalizeStructureClass.Class4SchemaMismatch)
            .Select(diff => diff.LogicalFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var highRisk = allDiffs
            .Where(diff => diff.Class is LocalizeStructureClass.Class3FieldPathMismatch
                or LocalizeStructureClass.Class4SchemaMismatch
                or LocalizeStructureClass.Class5ParserUnsupported)
            .OrderBy(diff => diff.Class)
            .ThenBy(diff => diff.LogicalFile, StringComparer.OrdinalIgnoreCase)
            .GroupBy(diff => diff.Class)
            .SelectMany(group => group.Take(options.MaxExamplesPerClass))
            .ToList();
        var fieldPathMismatchExamples = allDiffs
            .Where(diff => diff.FieldPathMismatchCount > 0)
            .OrderByDescending(diff => diff.FieldPathMismatchCount)
            .Take(options.MaxExamplesPerClass)
            .ToList();

        var oldChinese = AuditOldChineseCoverage(options, krOnly, parser, files, notes);

        return new LocalizeStructureAuditReport
        {
            TimestampUtc = DateTime.UtcNow,
            LocalizeRoot = options.LocalizeRoot,
            KrFileCount = kr.Count,
            EnFileCount = en.Count,
            JpFileCount = jp.Count,
            KrOnlyFiles = krOnly,
            EnOnlyFiles = enOnly,
            JpOnlyFiles = jpOnly,
            CommonKrEnFiles = commonKrEn,
            CommonKrJpFiles = commonKrJp,
            CommonAllFiles = commonAll,
            ClassCountsKrEn = CountByClass(krEnDiffs),
            ClassCountsKrJp = CountByClass(krJpDiffs),
            AffectedUnitCountsKrEn = SumAffectedUnits(krEnDiffs),
            AffectedUnitCountsKrJp = SumAffectedUnits(krJpDiffs),
            HighRiskExamples = highRisk,
            ParserUnsupportedFiles = parserUnsupported,
            FieldPathMismatchExamples = fieldPathMismatchExamples,
            RootKindMismatchFiles = rootKindMismatch,
            AffectedUnitKeyTotal = allDiffs.Sum(diff => diff.LeftOnlyUnits + diff.RightOnlyUnits + diff.FieldPathMismatchCount),
            OldChineseRoot = oldChinese.Root,
            KrOnlyFilesWithOldChinese = oldChinese.FilesWithOldChinese,
            KrOnlyFilesAllKeysMatched = oldChinese.AllKeysMatched,
            KrOnlyFilesWithKeyMismatch = oldChinese.KeyMismatch,
            KrOnlyFileMatchedKeyTotal = oldChinese.MatchedKeys,
            KrOnlyFileMismatchedKeyTotal = oldChinese.MismatchedKeys,
            KrOnlyFileOldChineseExamples = oldChinese.Examples,
            Notes = notes,
        };
        // ==== HELPERS MARKER ====
    }

    /// <summary>枚举某语言目录下的逻辑文件（只读；语言前缀已归一化）。</summary>
    private static List<(string Logical, string Full)> Scan(SourceLanguage language, string localizeRoot, List<string> notes)
    {
        var directory = Path.Combine(localizeRoot, SourceLanguageHelper.GetLocalizeDirectoryName(language));
        if (!Directory.Exists(directory))
        {
            notes.Add($"未找到 {SourceLanguageHelper.ToCode(language)} 目录：{directory}");
            return new List<(string, string)>();
        }

        var result = new List<(string Logical, string Full)>();
        foreach (var physical in LanguageFileMapper.ScanPhysicalFiles(directory))
        {
            var logical = LanguageFileMapper.ToCanonicalRelativePath(physical);
            result.Add((logical, Path.Combine(directory, physical.Replace('/', Path.DirectorySeparatorChar))));
        }

        return result;
    }

    /// <summary>解析单个文件（原始根类型 / 记录数 + 项目 Parser 的 TranslationUnit）。只读。</summary>
    private static ParsedFile AnalyzeFile(JsonGameFileParser parser, SourceLanguage language, string logical, string fullPath)
    {
        var parsed = new ParsedFile { LogicalFile = logical, Language = language };
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
            parsed = parsed with { RootKind = document.RootElement.ValueKind };
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("dataList", out var dataList)
                && dataList.ValueKind == JsonValueKind.Array)
            {
                parsed = parsed with { RecordCount = dataList.GetArrayLength() };
            }
        }
        catch (Exception ex)
        {
            return parsed with { ParseFailed = true, Error = $"JSON 解析失败: {ex.Message}" };
        }

        try
        {
            return parsed with { Units = parser.Parse(fullPath, logical) };
        }
        catch (Exception ex)
        {
            return parsed with { ParseFailed = true, Error = $"Parser 失败: {ex.Message}" };
        }
    }

    /// <summary>语义键 = RecordId + 字段名（用于识别「同一语义单元但数组下标不同」）。</summary>
    private static Dictionary<string, string> SemanticMap(IReadOnlyList<TranslationUnit> units)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            var fieldPath = unit.Key.FieldPath;
            var separator = fieldPath.LastIndexOf('.');
            var fieldName = separator >= 0 ? fieldPath[(separator + 1)..] : fieldPath;
            map.TryAdd($"{unit.Key.RecordId}|{fieldName}", fieldPath);
        }

        return map;
    }

    private static IReadOnlyDictionary<int, int> CountByClass(IReadOnlyList<LocalizeStructurePairDiff> diffs)
        => Enumerable.Range(0, 6).ToDictionary(index => index, index => diffs.Count(diff => (int)diff.Class == index));

    private static IReadOnlyDictionary<int, int> SumAffectedUnits(IReadOnlyList<LocalizeStructurePairDiff> diffs)
        => Enumerable.Range(0, 6).ToDictionary(
            index => index,
            index => diffs.Where(diff => (int)diff.Class == index)
                .Sum(diff => diff.LeftOnlyUnits + diff.RightOnlyUnits + diff.FieldPathMismatchCount));

    /// <summary>对「同一文件的两种语言」做结构分级（Class 0～5）。</summary>
    private static LocalizeStructurePairDiff Classify(ParsedFile left, ParsedFile right, int maxExamples)
    {
        // Class 4：两侧 JSON 都成功解析，但根类型不同（对象 / 数组）⇒ Schema 明显不同
        if (left.RootKind != JsonValueKind.Undefined
            && right.RootKind != JsonValueKind.Undefined
            && left.RootKind != right.RootKind)
        {
            return Build(left, right, LocalizeStructureClass.Class4SchemaMismatch,
                $"JSON 根类型不同：{left.RootKind} vs {right.RootKind}");
        }

        if (left.ParseFailed || right.ParseFailed)
        {
            var failure = left.ParseFailed ? left.Error : right.Error;
            return Build(left, right, LocalizeStructureClass.Class5ParserUnsupported, failure);
        }

        if (left.Units.Count == 0 && right.Units.Count == 0)
        {
            return Build(left, right, LocalizeStructureClass.Class0Identical, "两侧均无可翻译单元");
        }

        if (left.Units.Count == 0 || right.Units.Count == 0)
        {
            return Build(left, right, LocalizeStructureClass.Class5ParserUnsupported, "一侧无可翻译单元（无法按 Unit 对齐）");
        }

        var leftKeys = left.Units.Select(unit => unit.Key.ToString()).ToHashSet(StringComparer.Ordinal);
        var rightKeys = right.Units.Select(unit => unit.Key.ToString()).ToHashSet(StringComparer.Ordinal);
        var leftOnly = leftKeys.Except(rightKeys, StringComparer.Ordinal).Count();
        var rightOnly = rightKeys.Except(leftKeys, StringComparer.Ordinal).Count();

        if (leftKeys.SetEquals(rightKeys))
        {
            var leftTexts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var unit in left.Units)
            {
                leftTexts[unit.Key.ToString()] = unit.SourceText;
            }

            var identical = right.Units.All(unit =>
                leftTexts.TryGetValue(unit.Key.ToString(), out var text)
                && string.Equals(text, unit.SourceText, StringComparison.Ordinal));

            return Build(
                left, right,
                identical ? LocalizeStructureClass.Class0Identical : LocalizeStructureClass.Class1TextOnly,
                identical ? "完全同构（结构 + 文本一致）" : "结构一致，仅文本不同（正常多语言差异）");
        }

        var leftSemantic = SemanticMap(left.Units);
        var rightSemantic = SemanticMap(right.Units);
        var mismatchExamples = new List<string>();
        foreach (var pair in leftSemantic)
        {
            if (rightSemantic.TryGetValue(pair.Key, out var rightPath)
                && !string.Equals(pair.Value, rightPath, StringComparison.Ordinal))
            {
                mismatchExamples.Add($"{pair.Key}: {pair.Value} vs {rightPath}");
            }
        }

        mismatchExamples = mismatchExamples.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var structureClass = mismatchExamples.Count > 0
            ? LocalizeStructureClass.Class3FieldPathMismatch
            : LocalizeStructureClass.Class2UnitCountDiffers;

        return Build(
            left, right, structureClass,
            structureClass == LocalizeStructureClass.Class3FieldPathMismatch
                ? "存在同一语义单元但数组下标 / FieldPath 不同"
                : "UnitKey 数量不同（权威模板可独立输出）",
            leftOnly, rightOnly, mismatchExamples.Count, mismatchExamples.Take(maxExamples).ToList());
    }

    private static LocalizeStructurePairDiff Build(
        ParsedFile left,
        ParsedFile right,
        LocalizeStructureClass structureClass,
        string? note,
        int leftOnlyUnits = 0,
        int rightOnlyUnits = 0,
        int fieldPathMismatchCount = 0,
        IReadOnlyList<string>? fieldPathMismatchExamples = null)
        => new()
        {
            LogicalFile = left.LogicalFile,
            LeftLanguage = left.Language,
            RightLanguage = right.Language,
            Class = structureClass,
            LeftUnits = left.Units.Count,
            RightUnits = right.Units.Count,
            LeftRecords = left.RecordCount,
            RightRecords = right.RecordCount,
            LeftOnlyUnits = leftOnlyUnits,
            RightOnlyUnits = rightOnlyUnits,
            FieldPathMismatchCount = fieldPathMismatchCount,
            FieldPathMismatchExamples = fieldPathMismatchExamples ?? Array.Empty<string>(),
            Note = note,
        };

    /// <summary>
    /// KR-only 文件的旧中文覆盖统计：当前 KR 有、EN 没有的整文件，
    /// 若旧中文目录存在同名文件，其 UnitKey 是否能与 KR 对齐（决定 P2-η 修复在真实数据上是否真的有效）。
    /// </summary>
    private static OldChineseCoverage AuditOldChineseCoverage(
        LocalizeStructureAuditOptions options,
        IReadOnlyList<string> krOnlyFiles,
        JsonGameFileParser parser,
        IReadOnlyDictionary<SourceLanguage, List<(string Logical, string Full)>> files,
        List<string> notes)
    {
        var root = options.OldChineseRoot;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            notes.Add("未提供可用的旧中文树（--audit-old-zh-root），跳过「KR-only 文件旧中文覆盖」统计。");
            return new OldChineseCoverage { Root = root, Examples = Array.Empty<string>() };
        }

        var krFiles = files[SourceLanguage.Korean];
        var withOldChinese = 0;
        var allMatched = 0;
        var keyMismatch = 0;
        var matchedTotal = 0;
        var mismatchedTotal = 0;
        var examples = new List<string>();

        foreach (var logical in krOnlyFiles)
        {
            var zhPath = Path.Combine(root, logical.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(zhPath))
            {
                continue;
            }

            withOldChinese++;
            try
            {
                var krEntry = krFiles.FirstOrDefault(file => string.Equals(file.Logical, logical, StringComparison.OrdinalIgnoreCase));
                var krUnits = krEntry.Full is null ? new List<TranslationUnit>() : parser.Parse(krEntry.Full, logical);
                var zhUnits = parser.Parse(zhPath, logical);
                var krKeys = krUnits.Select(unit => unit.Key.ToString()).ToHashSet(StringComparer.Ordinal);
                var zhKeys = zhUnits.Select(unit => unit.Key.ToString()).ToHashSet(StringComparer.Ordinal);
                var matched = krKeys.Count(key => zhKeys.Contains(key));
                var missing = krKeys.Count - matched;

                matchedTotal += matched;
                mismatchedTotal += missing;
                if (missing == 0)
                {
                    allMatched++;
                }
                else
                {
                    keyMismatch++;
                }

                if (examples.Count < 20)
                {
                    examples.Add($"{logical}: KR 单元 {krKeys.Count}｜旧中文匹配 {matched}｜未匹配 {missing}");
                }
            }
            catch (Exception ex)
            {
                notes.Add($"KR-only 文件旧中文解析失败：{logical}（{ex.Message}）");
            }
        }

        return new OldChineseCoverage
        {
            Root = root,
            FilesWithOldChinese = withOldChinese,
            AllKeysMatched = allMatched,
            KeyMismatch = keyMismatch,
            MatchedKeys = matchedTotal,
            MismatchedKeys = mismatchedTotal,
            Examples = examples,
        };
    }
}

/// <summary>单文件解析结果（只读审计中间结果）。</summary>
internal sealed record ParsedFile
{
    public required string LogicalFile { get; init; }
    public required SourceLanguage Language { get; init; }
    public bool ParseFailed { get; init; }
    public string? Error { get; init; }
    public JsonValueKind RootKind { get; init; } = JsonValueKind.Undefined;
    public int RecordCount { get; init; } = -1;
    public IReadOnlyList<TranslationUnit> Units { get; init; } = Array.Empty<TranslationUnit>();
}

/// <summary>KR-only 文件 × 旧中文覆盖统计。</summary>
internal sealed class OldChineseCoverage
{
    public string? Root { get; init; }
    public int FilesWithOldChinese { get; init; }
    public int AllKeysMatched { get; init; }
    public int KeyMismatch { get; init; }
    public int MatchedKeys { get; init; }
    public int MismatchedKeys { get; init; }
    public required IReadOnlyList<string> Examples { get; init; }
}
