using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Persistence;

namespace LimbusTranslator.Infrastructure.Review;

/// <summary>
/// 人工审核写回服务（第1轮最小闭环）。
///
/// 职责：
///   把用户在“待审核”列表中确认过的人工译文写回 Translation Memory，
///   来源记为 TranslationSource.HumanReviewed、NeedsReview=false，
///   使下一次运行同 UnitKey + SourceHash 时直接命中，不再调用 Provider。
///
/// 本轮不做完整 Review State Machine（Pending / Approved / Rejected / Locked 等留待后续）。
/// </summary>
public static class HumanReviewService
{
    /// <summary>
    /// 写回人工审核结果。
    /// </summary>
    /// <param name="entries">审核列表（用户在 UI 中确认过的条目）</param>
    /// <param name="memory">Translation Memory</param>
    /// <returns>成功写回条数</returns>
    public static int SaveReviewedEntries(IEnumerable<DiffEntry> entries, ITranslationMemory memory)
    {
        var saved = 0;
        foreach (var entry in entries)
        {
            if (entry.Action == TranslationAction.SkipDeleted)
            {
                continue;
            }

            var unit = TranslationUnitFactory.FromDiffEntry(entry);

            // fail-safe：空源文不入库；空译文不入库（避免把“已审核但没填内容”写成有效人工译文）
            if (!SourceTextGuard.IsReusable(unit.SourceText))
            {
                continue;
            }

            if (!memory.SaveHumanReviewed(unit, entry.Translation ?? string.Empty))
            {
                continue;
            }

            // 人工确认后：来源变为 HumanReviewed，并清除待审核标记（ReviewReason 一并清除）
            entry.Provenance = TranslationSource.HumanReviewed;
            entry.NeedsReview = false;
            entry.ReviewReason = null;
            saved++;
        }

        return saved;
    }

    /// <summary>
    /// 第9.0C.24轮：**高效批量写回（单事务）**。
    ///
    /// 语义与 <see cref="SaveReviewedEntries"/> **完全一致**（来源=HumanReviewed、NeedsReview=false、
    /// 跳过 SkipDeleted / 空源文 / 空译文，并同步把条目标记为人工确认），
    /// 但用 <see cref="SqliteTranslationMemory.SaveMany"/> 一次事务写完。
    ///
    /// 【为什么必须有】真实故障：旧实现逐条 <c>SaveHumanReviewed</c> ⇒ **每条一个新连接 + 一个新事务**，
    /// 「审核后重新输出」对 10 万条回写时把 UI 线程卡死好几分钟。
    /// </summary>
    /// <returns>成功写回条数</returns>
    public static int SaveReviewedEntriesBulk(
        IEnumerable<DiffEntry> entries,
        SqliteTranslationMemory memory)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(memory);

        var pending = new List<(DiffEntry Entry, TranslationUnit Unit)>();
        foreach (var entry in entries)
        {
            if (entry.Action == TranslationAction.SkipDeleted)
            {
                continue;
            }

            var unit = TranslationUnitFactory.FromDiffEntry(entry);
            if (!SourceTextGuard.IsReusable(unit.SourceText))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Translation))
            {
                continue;
            }

            pending.Add((entry, unit));
        }

        if (pending.Count == 0)
        {
            return 0;
        }

        var saved = memory.SaveMany(pending.Select(pair => (
            Unit: pair.Unit,
            Result: new TranslationResult
            {
                Key = pair.Entry.Key,
                Translation = pair.Entry.Translation ?? string.Empty,
                Source = TranslationSource.HumanReviewed,
                NeedsReview = false,
                ReviewReason = null,
                TmMatchType = TranslationMemoryMatchType.None,
                Issues = pair.Entry.ValidationIssues,
            })));

        // 与逐条版保持一致：写回成功后把条目标记为人工确认（来源=HumanReviewed、清除待审标记）
        foreach (var pair in pending)
        {
            pair.Entry.Provenance = TranslationSource.HumanReviewed;
            pair.Entry.NeedsReview = false;
            pair.Entry.ReviewReason = null;
        }

        return saved;
    }
}
