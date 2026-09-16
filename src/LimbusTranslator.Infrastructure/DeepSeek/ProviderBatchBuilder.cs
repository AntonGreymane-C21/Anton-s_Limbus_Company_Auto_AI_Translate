using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// Provider 请求分批器（第7.5轮）。
///
/// 这是「Provider Request Batch」的**唯一**切分实现：
///   - 输入：某个 Stage（同文件）的全部待翻译条目（顺序即上游顺序）
///   - 输出：按 <see cref="BatchOptions"/> 切分后的批次列表（顺序稳定、可复现）
///
/// 约束：
///   - 单条超过字符上限时**单独成批**（不截断、不丢条目、不死循环）；
///   - 空输入返回空列表；
///   - 不依赖 Dictionary/Sort，不依赖并发顺序：相同输入必得相同批次边界与批内顺序
///     （批次顺序会进入 RequestFingerprint）。
/// </summary>
public static class ProviderBatchBuilder
{
    /// <summary>
    /// 按配置切分批次。
    /// </summary>
    /// <param name="entries">待翻译条目（顺序敏感）</param>
    /// <param name="options">分批配置</param>
    /// <param name="log">调试日志回调（用于记录「单条超上限单独提交」）</param>
    public static IReadOnlyList<IReadOnlyList<DiffEntry>> Build(
        IReadOnlyList<DiffEntry> entries,
        BatchOptions options,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(options);

        var maxItems = options.MaxItemsPerBatch > 0
            ? options.MaxItemsPerBatch
            : BatchOptions.DefaultMaxItemsPerBatch;
        var maxCharacters = options.MaxCharactersPerBatch > 0
            ? options.MaxCharactersPerBatch
            : BatchOptions.DefaultMaxCharactersPerBatch;

        var batches = new List<IReadOnlyList<DiffEntry>>();
        var current = new List<DiffEntry>();
        var currentCharacters = 0;

        foreach (var entry in entries)
        {
            var length = BatchOptions.MeasureItemCharacters(entry);

            // 超过条目阈值或字符预算时切分（current.Count > 0 保证单条超长不会产生空批）
            if (current.Count >= maxItems
                || (current.Count > 0 && currentCharacters + length > maxCharacters))
            {
                batches.Add(current);
                current = new List<DiffEntry>();
                currentCharacters = 0;
            }

            if (current.Count == 0 && length > maxCharacters)
            {
                // 单条本身就超过字符预算：单独成批（不截断原文，是否可被 API 接受由 Provider/API 决定）
                log?.Invoke(
                    $"[调试] 单条翻译文本超过批次字符上限（{length} > {maxCharacters}），将单独提交：" +
                    $"UnitKey={entry.Key}");
            }

            current.Add(entry);
            currentCharacters += length;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }
}
