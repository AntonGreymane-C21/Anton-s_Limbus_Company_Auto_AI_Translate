using System.Text.Json;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>从 output 目录读取到的进度（第9.0C.8轮）。</summary>
public sealed class OutputProgressLoadResult
{
    /// <summary>UnitKey 字符串 → 当前译文（只含在 output 中确实找到的条目）。</summary>
    public required IReadOnlyDictionary<string, string> Translations { get; init; }

    /// <summary>output 中实际读取到的文件数。</summary>
    public required int FileCount { get; init; }

    /// <summary>条目所属文件在 output 中不存在的条目数（= 尚未写出的部分）。</summary>
    public required int MissingFileEntryCount { get; init; }

    /// <summary>字段路径解析失败的条目数（结构不一致 / 非字符串字段）。</summary>
    public required int UnresolvedFieldCount { get; init; }

    /// <summary>一行式摘要（日志用）。</summary>
    public string Describe()
        => $"output 文件 {FileCount} 个；命中条目 {Translations.Count} 条"
           + $"（文件缺失 {MissingFileEntryCount} 条，字段未解析 {UnresolvedFieldCount} 条）";
}

/// <summary>
/// 「从 output 载入当前汉化进度」（第9.0C.8轮）—— 只读 output 目录，不调用 API、不改任何文件。
///
/// 为什么需要：工具产出的是 `data/output/**.json`，但界面状态（Diff 统计 / 待审核列表 / 门禁 / 部署清单）
/// 全部绑定在"本轮 Run"上；重启程序后进度就看不见了。本类把 output 里**已经写好的译文**按 UnitKey 读回来，
/// 供 ViewModel 覆盖到生产计划的条目上（来源=Imported），并可顺带写入 TM（下次翻译直接命中）。
///
/// 关键实现点：
///   - **按文件分组、每个文件只解析一次**（真实数据 908 个文件 / 8 万条，逐条读文件不可接受）；
///   - 字段路径用与 Merge 相同的 `dataList[3].content` 语义解析（<see cref="TryResolveFieldPath"/>）；
///   - 只有**字符串**字段才算译文（数字 / 布尔等结构字段不会被误当成译文）。
/// </summary>
public static class OutputProgressLoader
{
    /// <summary>
    /// 从 <paramref name="outputRoot"/> 读取 <paramref name="entries"/> 的当前译文。
    /// </summary>
    public static OutputProgressLoadResult Load(string outputRoot, IReadOnlyList<DiffEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot) || entries.Count == 0)
        {
            return new OutputProgressLoadResult
            {
                Translations = translations,
                FileCount = 0,
                MissingFileEntryCount = 0,
                UnresolvedFieldCount = 0,
            };
        }

        var byFile = entries
            .Where(entry => entry is not null)
            .GroupBy(entry => entry.Key.RelativeFilePath, StringComparer.Ordinal)
            .ToList();

        var fileCount = 0;
        var missingFileEntries = 0;
        var unresolvedFields = 0;

        foreach (var group in byFile)
        {
            var path = Path.Combine(outputRoot, group.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                missingFileEntries += group.Count();
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException)
            {
                missingFileEntries += group.Count();
                continue;
            }

            using (document)
            {
                fileCount++;
                foreach (var entry in group)
                {
                    if (TryResolveFieldPath(document.RootElement, entry.Key.FieldPath, out var text)
                        && !string.IsNullOrWhiteSpace(text))
                    {
                        translations[entry.Key.ToString()] = text!;
                    }
                    else
                    {
                        unresolvedFields++;
                    }
                }
            }
        }

        return new OutputProgressLoadResult
        {
            Translations = translations,
            FileCount = fileCount,
            MissingFileEntryCount = missingFileEntries,
            UnresolvedFieldCount = unresolvedFields,
        };
    }

    /// <summary>
    /// 解析字段路径（与 Merge 的写入语义一致）：<c>name</c> / <c>a.b</c> / <c>dataList[3].content</c>。
    /// </summary>
    public static bool TryResolveFieldPath(JsonElement root, string? fieldPath, out string? text)
    {
        text = null;
        if (string.IsNullOrWhiteSpace(fieldPath))
        {
            return false;
        }

        var current = root;
        foreach (var rawSegment in fieldPath.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = rawSegment;
            var index = -1;

            var bracket = rawSegment.IndexOf('[');
            if (bracket >= 0)
            {
                var close = rawSegment.IndexOf(']', bracket);
                if (close < 0)
                {
                    return false;
                }

                name = rawSegment[..bracket];
                if (!int.TryParse(rawSegment[(bracket + 1)..close], out index) || index < 0)
                {
                    return false;
                }
            }

            if (name.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
                {
                    return false;
                }
            }

            if (index >= 0)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return false;
                }

                current = current[index];
            }
        }

        if (current.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        text = current.GetString();
        return true;
    }
}
