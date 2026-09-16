using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Release;

/// <summary>
/// 发布门禁结果。
///
/// 与 <see cref="ValidationReport"/> 的关系：ValidationReport 说明“发现了什么”，
/// 本结果说明“这些发现对发布意味着什么”。
/// </summary>
public sealed class ReleaseGateResult
{
    /// <summary>门禁结论</summary>
    public required ReleaseGateStatus Status { get; init; }

    /// <summary>参与评估的条目数（不含 SkipDeleted）</summary>
    public required int EvaluatedEntryCount { get; init; }

    /// <summary>Error 总数</summary>
    public required int ErrorCount { get; init; }

    /// <summary>Warning 总数</summary>
    public required int WarningCount { get; init; }

    /// <summary>待审核条目数</summary>
    public required int NeedsReviewCount { get; init; }

    /// <summary>导致 Blocked 的 Error 数</summary>
    public required int BlockingErrorCount { get; init; }

    /// <summary>历史继承旧中文的结构安全差异条目数</summary>
    public required int HistoricalInheritedErrorCount { get; init; }

    /// <summary>
    /// 权威结构里存在、但 output 缺失的 Key 数（第9.0B-P4轮；未传 Key 集时恒为 0）。
    /// </summary>
    public int MissingExpectedKeyCount { get; init; }

    /// <summary>
    /// output 里出现权威结构不存在的 Key 数（第9.0B-P4轮；未传 Key 集时恒为 0）。
    /// </summary>
    public int UnexpectedOutputKeyCount { get; init; }

    /// <summary>结构化原因列表</summary>
    public required IReadOnlyList<ReleaseGateReason> Reasons { get; init; }

    /// <summary>可直接展示的阻断原因文本（[原因] 说明，逐条一行）</summary>
    public IReadOnlyList<string> BlockingReasons => Reasons
        .Where(r => r.Escalation != ReleaseGateStatus.Passed)
        .Select(r => r.Message)
        .ToArray();

    /// <summary>
    /// 需要人工确认时的原因摘要（Passed / Blocked 时为 null）。
    /// </summary>
    public string? RequiresConfirmationReason
        => Status != ReleaseGateStatus.RequiresConfirmation || Reasons.Count == 0
            ? null
            : string.Join("；", Reasons.Where(r => r.Escalation == ReleaseGateStatus.RequiresConfirmation)
                .Select(r => r.Message));

    /// <summary>是否存在阻断性 Error</summary>
    public bool IsBlocked => Status == ReleaseGateStatus.Blocked;
}
