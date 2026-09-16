using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;

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
}
