using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Infrastructure.Translation;

/// <summary>
/// 一次「锁定术语自动修正」的结果（第9.0C.2轮）。
/// </summary>
public sealed class LockedTerminologyRepairOutcome
{
    /// <summary>是否真的发起了修正请求（false = 未触发 / 被跳过）</summary>
    public bool Attempted { get; init; }

    /// <summary>本次尝试次数（0 或 1；本轮硬上限 1）</summary>
    public int AttemptCount { get; init; }

    /// <summary>修正后译文是否通过**完整**校验链（即最终被采用）</summary>
    public bool Succeeded { get; init; }

    /// <summary>被采用的最终译文（未采用时为 null）</summary>
    public string? Translation { get; init; }

    /// <summary>触发时发现的锁定术语违规（结构化对象）</summary>
    public IReadOnlyList<LockedTermViolation> Violations { get; init; } = Array.Empty<LockedTermViolation>();

    /// <summary>修正后仍然存在的锁定术语违规</summary>
    public IReadOnlyList<LockedTermViolation> RemainingViolations { get; init; } = Array.Empty<LockedTermViolation>();

    /// <summary>修正请求的逻辑 RequestId（用于 request_cache 落库 / 丢弃）</summary>
    public string? RequestId { get; init; }

    /// <summary>修正请求是否命中缓存</summary>
    public bool CacheHit { get; init; }

    /// <summary>未触发 / 跳过 / 失败原因（诊断用）</summary>
    public string? Note { get; init; }

    /// <summary>可读的日志摘要</summary>
    public string Describe()
        => Attempted
            ? $"尝试{AttemptCount}次，{(Succeeded ? "成功" : "仍违规")}，"
              + $"原违规 {Violations.Count} 条，剩余 {RemainingViolations.Count} 条"
            : $"未触发（{Note ?? "无需修正"}）";
}
