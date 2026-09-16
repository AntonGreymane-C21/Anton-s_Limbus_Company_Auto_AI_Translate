namespace LimbusTranslator.Core.Models;

/// <summary>
/// 可缓存的 Provider 批量响应（第4轮）。
///
/// 缓存的是 **Provider 尚未完成最终后处理** 的版本化响应 DTO：
///   Items[].Translation 仍是模型原始输出（Placeholder 保护文本，未恢复）。
/// 这样 Cache Hit 之后必然走与网络响应完全相同的后处理链：
///   Placeholder Restore → ValidatorPipeline → NeedsReview → TM Save。
///
/// 【禁止】把最终译文直接当作缓存对象，否则会绕过 Placeholder / Validator / Provenance。
/// </summary>
public sealed class CachedProviderBatchResponse
{
    /// <summary>缓存响应格式版本（存于 ResponseJson 内；不支持的版本视为 Cache Miss）</summary>
    public int FormatVersion { get; init; } = CurrentFormatVersion;

    /// <summary>当前支持的格式版本</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>Provider 标识（如 deepseek）</summary>
    public required string Provider { get; init; }

    /// <summary>模型名</summary>
    public required string Model { get; init; }

    /// <summary>写入时间（UTC，诊断用；不参与指纹计算）</summary>
    public DateTime CreatedAtUtc { get; init; }

    /// <summary>Provider 返回的响应 Id（可空）</summary>
    public string? ResponseId { get; init; }

    /// <summary>Provider 返回的模型名（可空）</summary>
    public string? ResponseModel { get; init; }

    /// <summary>输入 token（可空）</summary>
    public int? PromptTokens { get; init; }

    /// <summary>输出 token（可空）</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>总 token（可空）</summary>
    public int? TotalTokens { get; init; }

    /// <summary>
    /// 隐藏推理 token（第8.5轮）。
    /// 旧缓存（第8轮写出的 ResponseJson）没有该字段 → 反序列化为 null，**向后兼容，不会失效**。
    /// </summary>
    public int? ReasoningTokens { get; init; }

    /// <summary>响应条目（顺序与请求一致）</summary>
    public required IReadOnlyList<CachedProviderItem> Items { get; init; }
}

/// <summary>缓存响应中的单条结果（模型原始输出）。</summary>
public sealed class CachedProviderItem
{
    /// <summary>请求使用的 id（与 UnitKey 一一对应）</summary>
    public required string Id { get; init; }

    /// <summary>模型原始输出（Placeholder 保护文本，未恢复）</summary>
    public required string Translation { get; init; }

    /// <summary>AI 自报 needs_review</summary>
    public bool NeedsReview { get; init; }

    /// <summary>AI 自报原因</summary>
    public string Reason { get; init; } = string.Empty;
}
