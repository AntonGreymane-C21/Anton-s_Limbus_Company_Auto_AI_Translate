using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Release;

/// <summary>
/// 发布门禁策略（来源感知）。
///
/// 设计要点：
///   1. 门禁的“严格程度”集中在这里，而不是写死在 Validator 里；
///   2. 历史继承旧中文（Inherited）在首版取宽松策略（RequiresConfirmation），
///      因为它们的结构差异早于 Validator 上线，直接 Blocked 会把历史版本整体锁死；
///   3. 将来历史译文清理完成后，只需把 <see cref="InheritedHardSafetyError"/> 改成
///      <see cref="ReleaseGateStatus.Blocked"/>（或在调用处传入新策略），无需改任何 Validator。
/// </summary>
public sealed class ReleaseGatePolicy
{
    /// <summary>默认策略（第3轮首版）。</summary>
    public static ReleaseGatePolicy Default { get; } = new();

    /// <summary>
    /// 新译文（AI / Mock / TranslationMemory / RequestCache / 未知来源）出现硬安全 Error 时的结论。
    /// 默认：Blocked（不得通过人工“忽略一下”直接部署）。
    /// </summary>
    public ReleaseGateStatus NewTranslationHardError { get; init; } = ReleaseGateStatus.Blocked;

    /// <summary>
    /// 人工 / 官方译文出现硬安全 Error 时的结论。
    /// 默认：Blocked（人工审核不是跳过结构安全校验的通行证）。
    /// </summary>
    public ReleaseGateStatus ReviewedHardError { get; init; } = ReleaseGateStatus.Blocked;

    /// <summary>
    /// 历史继承旧中文出现硬安全 Error 时的结论。
    /// 默认：RequiresConfirmation（首版历史兼容；未来可收紧为 Blocked）。
    /// </summary>
    public ReleaseGateStatus InheritedHardSafetyError { get; init; } = ReleaseGateStatus.RequiresConfirmation;

    /// <summary>新译文存在启发式 Warning 时的结论（默认 RequiresConfirmation）。</summary>
    public ReleaseGateStatus NewTranslationWarning { get; init; } = ReleaseGateStatus.RequiresConfirmation;

    /// <summary>存在待审核条目时的结论（默认 RequiresConfirmation）。</summary>
    public ReleaseGateStatus NeedsReview { get; init; } = ReleaseGateStatus.RequiresConfirmation;

    /// <summary>
    /// 是否把历史继承旧中文的启发式 Warning 计入 Warning 汇总（默认计入，但不升级门禁状态）。
    /// </summary>
    public bool CountInheritedWarnings { get; init; } = true;

    /// <summary>
    /// 是否把人工 / 官方译文的启发式 Warning 计入 Warning 汇总（默认计入，但不影响人工确认结论）。
    /// </summary>
    public bool CountReviewedWarnings { get; init; } = true;

    /// <summary>
    /// 权威结构里存在、但 output 缺失 Key 时的结论（第9.0B-P4轮）。默认：Blocked（结构不完整不得部署）。
    /// </summary>
    public ReleaseGateStatus MissingExpectedKey { get; init; } = ReleaseGateStatus.Blocked;

    /// <summary>
    /// output 里出现权威结构不存在的 Key 时的结论（第9.0B-P4轮）。默认：Blocked（残留旧 Key 不得部署）。
    /// </summary>
    public ReleaseGateStatus UnexpectedOutputKey { get; init; } = ReleaseGateStatus.Blocked;

    /// <summary>合并两个结论，取更严格的一方。</summary>
    public static ReleaseGateStatus Escalate(ReleaseGateStatus current, ReleaseGateStatus candidate)
        => (int)candidate > (int)current ? candidate : current;
}
