using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Diagnostics;

/// <summary>
/// Thinking A/B 人工审阅的单组不可变快照。
///
/// A/B Harness 的 <see cref="DiffEntry"/> 是可变运行对象；必须在每组校验完成后立即捕获，
/// 不能在另一组运行后再从同一个 Entry 读取审核状态，否则会把另一组的 Issue 误贴到本组译文上。
/// 本类型只服务于诊断报告，不参与翻译、TM、缓存或部署。
/// </summary>
public sealed class ThinkingAbReviewSnapshot
{
    public required string UnitKey { get; init; }

    public required string SourceText { get; init; }

    /// <summary>本组 Placeholder 恢复后的最终译文。</summary>
    public required string Translation { get; init; }

    public TranslationSource? Provenance { get; init; }

    public bool NeedsReview { get; init; }

    public string? ReviewReason { get; init; }

    /// <summary>复制为数组，避免后续替换 Entry.ValidationIssues 时污染历史快照。</summary>
    public IReadOnlyList<ValidationIssue> ValidationIssues { get; init; } = Array.Empty<ValidationIssue>();

    public static ThinkingAbReviewSnapshot Capture(DiffEntry entry, TranslationResult result)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(result);

        return new ThinkingAbReviewSnapshot
        {
            UnitKey = entry.Key.ToString(),
            SourceText = entry.NewSourceText ?? entry.OldSourceText ?? string.Empty,
            Translation = result.Translation,
            Provenance = entry.Provenance,
            NeedsReview = entry.NeedsReview,
            ReviewReason = entry.ReviewReason,
            ValidationIssues = entry.ValidationIssues.ToArray(),
        };
    }
}
