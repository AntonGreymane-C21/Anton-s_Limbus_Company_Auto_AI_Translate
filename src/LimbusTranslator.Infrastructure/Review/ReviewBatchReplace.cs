using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Review;

/// <summary>批量替换的生效范围（第9.0C.7轮）。</summary>
public enum ReviewReplaceScope
{
    /// <summary>全部已加载条目（默认；= 本轮翻译条目）</summary>
    AllLoaded = 0,

    /// <summary>当前筛选 / 搜索结果</summary>
    CurrentFilter,

    /// <summary>当前条目所在文件</summary>
    CurrentFile,
}

/// <summary>命中样例（预览用：一眼看出会被怎么改）。</summary>
public sealed record ReviewReplaceSample(string UnitKey, string Before, string After);

/// <summary>批量替换预览结果。</summary>
public sealed record ReviewReplacePreview(
    int EntryCount,
    int OccurrenceCount,
    IReadOnlyList<ReviewReplaceSample> Samples)
{
    /// <summary>是否一个都没命中。</summary>
    public bool IsEmpty => EntryCount == 0 || OccurrenceCount == 0;

    /// <summary>可直接展示的中文摘要。</summary>
    public string Describe()
        => IsEmpty
            ? "没有命中任何条目（请检查查找文本与范围）。"
            : $"命中 {EntryCount} 条 / 共 {OccurrenceCount} 处";
}

/// <summary>
/// 待审核页的**批量替换**（第9.0C.7轮）—— 纯函数、无副作用、不碰数据库 / 术语库。
///
/// 设计取舍：
///   - 只做**字面量**替换（不做正则、不做词边界）：用户要的是"把我确认过的错译统一改掉"，
///     正则/词边界会带来不可预期的命中；
///   - 只在**译文**上替换；源文 / 邻句 / 术语一概不动；
///   - 命中条目由调用方送去重跑 ValidationPipeline（替换可能把 {0} / 标签 / 数字弄坏 ⇒ 必须重校验）；
///   - 本类不负责持久化：写回 TM（HumanReviewed）与术语库由调用方走既有服务。
/// </summary>
public static class ReviewBatchReplace
{
    /// <summary>
    /// 预览：统计会被改动的条目数、出现次数，并给出前若干条前后对照。
    /// </summary>
    public static ReviewReplacePreview Preview(
        IEnumerable<DiffEntry> entries,
        string? find,
        string? with,
        bool caseSensitive,
        int maxSamples = 10)
    {
        if (entries is null || string.IsNullOrEmpty(find))
        {
            return new ReviewReplacePreview(0, 0, Array.Empty<ReviewReplaceSample>());
        }

        var replacement = with ?? string.Empty;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var entryCount = 0;
        var occurrenceCount = 0;
        var samples = new List<ReviewReplaceSample>();

        foreach (var entry in entries)
        {
            var current = entry?.Translation;
            if (string.IsNullOrEmpty(current))
            {
                continue;
            }

            var hits = CountOccurrences(current, find, comparison);
            if (hits == 0)
            {
                continue;
            }

            entryCount++;
            occurrenceCount += hits;

            if (samples.Count < maxSamples)
            {
                samples.Add(new ReviewReplaceSample(
                    entry!.Key.ToString(),
                    Truncate(current),
                    Truncate(current.Replace(find, replacement, comparison))));
            }
        }

        return new ReviewReplacePreview(entryCount, occurrenceCount, samples);
    }

    /// <summary>
    /// 执行替换：**只**返回被实际改动的条目（未命中的条目原样跳过），调用方据此重校验与保存。
    /// </summary>
    public static IReadOnlyList<DiffEntry> Apply(
        IEnumerable<DiffEntry> entries,
        string? find,
        string? with,
        bool caseSensitive)
    {
        if (entries is null || string.IsNullOrEmpty(find))
        {
            return Array.Empty<DiffEntry>();
        }

        var replacement = with ?? string.Empty;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var changed = new List<DiffEntry>();

        foreach (var entry in entries)
        {
            var current = entry?.Translation;
            if (string.IsNullOrEmpty(current) || CountOccurrences(current, find, comparison) == 0)
            {
                continue;
            }

            var updated = current.Replace(find, replacement, comparison);
            if (string.Equals(updated, current, StringComparison.Ordinal))
            {
                continue;
            }

            entry!.Translation = updated;
            changed.Add(entry);
        }

        return changed;
    }

    /// <summary>统计子串出现次数（区分 / 不区分大小写）。</summary>
    public static int CountOccurrences(string text, string find, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(find))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while (index <= text.Length - find.Length)
        {
            var found = text.IndexOf(find, index, comparison);
            if (found < 0)
            {
                break;
            }

            count++;
            index = found + find.Length;
        }

        return count;
    }

    private static string Truncate(string text)
        => text.Length <= 60 ? text : text[..60] + "…";
}
