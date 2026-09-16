namespace LimbusTranslator.Core.Models;

/// <summary>
/// 锁定术语修正结果（第9.0C.2轮）。
/// </summary>
public sealed class LockedTerminologyRepairResult
{
    /// <summary>是否真的发起了修正请求（false = 未触发 / 被跳过）</summary>
    public bool Attempted { get; init; }

    /// <summary>修正后的完整译文（未成功时为 null）</summary>
    public string? Translation { get; init; }

    /// <summary>是否命中请求缓存（命中时未产生网络请求）</summary>
    public bool CacheHit { get; init; }

    /// <summary>本次修正请求的逻辑 RequestId（用于 request_cache 的暂存 / 落库 / 丢弃）</summary>
    public string? RequestId { get; init; }

    /// <summary>模型自报的待审核标记（原样传播，不与 Validator 结论混用）</summary>
    public bool NeedsReview { get; init; }

    /// <summary>模型自报的审核原因</summary>
    public string? ReviewReason { get; init; }

    /// <summary>修正响应携带的结构化问题（如 Placeholder 宽容恢复）</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = Array.Empty<ValidationIssue>();

    /// <summary>请求 Token（真实 API 返回；缓存命中时为 null）</summary>
    public int? InputTokens { get; init; }

    /// <summary>输出 Token</summary>
    public int? OutputTokens { get; init; }

    /// <summary>推理 Token（修正默认关闭 Thinking，通常为 null）</summary>
    public int? ReasoningTokens { get; init; }

    /// <summary>未触发或被跳过的原因（诊断用）</summary>
    public string? SkipReason { get; init; }
}
