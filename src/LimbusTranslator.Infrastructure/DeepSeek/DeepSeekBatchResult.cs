using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// 一次 DeepSeek 批量调用（逻辑请求）的完整结果：解析后的条目 + 响应元数据。
///
/// 元数据只用于 Trace / 诊断，不参与业务判断：
///   RetryCount / DurationMs 由客户端统计；
///   ResponseId / ResponseModel / Token 用量来自 OpenAI 兼容响应（缺失时为 null）。
/// </summary>
public sealed class DeepSeekBatchResult
{
    /// <summary>解析后的条目（key = 请求 id / UnitKey）</summary>
    public required IReadOnlyDictionary<string, DeepSeekTranslateItem> Items { get; init; }

    /// <summary>OpenAI 兼容响应 id（可空）</summary>
    public string? ResponseId { get; init; }

    /// <summary>响应中的模型名（可空）</summary>
    public string? ResponseModel { get; init; }

    /// <summary>输入 token（可空）</summary>
    public int? PromptTokens { get; init; }

    /// <summary>输出 token（可空）</summary>
    public int? CompletionTokens { get; init; }

    /// <summary>总 token（可空）</summary>
    public int? TotalTokens { get; init; }

    /// <summary>
    /// 隐藏推理 token（第8.5轮，来自 usage.completion_tokens_details.reasoning_tokens；缺失为 null）。
    /// </summary>
    public int? ReasoningTokens { get; init; }

    /// <summary>
    /// 模型真正输出的可见译文 token = CompletionTokens - ReasoningTokens（任一缺失为 null）。
    /// </summary>
    public int? VisibleOutputTokens =>
        CompletionTokens.HasValue && ReasoningTokens.HasValue
            ? Math.Max(0, CompletionTokens.Value - ReasoningTokens.Value)
            : null;

    /// <summary>本次逻辑请求实际发生的重试次数（首次成功为 0）</summary>
    public int RetryCount { get; init; }

    /// <summary>本次逻辑请求耗时（毫秒，含重试）</summary>
    public long DurationMs { get; init; }
}

/// <summary>
/// 单次请求的 Thinking 设置（第8.75轮）：由 TranslationThinkingPolicy 逐批决定，
/// 使自适应策略下同一 Stage 的 ON / OFF 条目不会混进同一个 API 请求。
/// </summary>
/// <param name="Enabled">是否开启思考模式（对应 thinking.type = enabled / disabled）</param>
/// <param name="ReasoningEffort">思考强度（low / high / max；关闭或未配置时为 null）</param>
public sealed record DeepSeekRequestThinking(bool Enabled, string? ReasoningEffort);

/// <summary>
/// DeepSeek 批量调用客户端抽象（第4轮最小测试缝）。
///
/// 目的：让 <see cref="DeepSeekTranslationProvider"/> 的缓存/追踪/后处理链能在**不访问真实 API**的情况下被测试。
/// 实现方只负责 HTTP / Retry / 协议解析 —— 禁止把 SQLite、ReleaseGate、Validator、StoryData 塞进来。
/// </summary>
public interface IDeepSeekBatchClient
{
    /// <summary>执行一次批量翻译请求（包含重试与协议校验）</summary>
    Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt);

    /// <summary>
    /// 带显式 Thinking 设置的批量翻译（第8.75轮）。
    ///
    /// 默认实现退化为不携带 Thinking 的旧方法，因此既有测试替身无需修改即可继续编译；
    /// 真实客户端 <see cref="DeepSeekClient"/> 覆盖本方法，把 thinking 真正写入 HTTP 请求体。
    /// </summary>
    Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking)
        => TranslateBatchWithMetadataAsync(batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt);

    /// <summary>
    /// 第8.87轮：同时携带 Thinking 决策与「旧译文规则 / 分类指令」的批量翻译。
    ///
    /// 默认实现退化为不带这两项的重载，因此既有测试替身无需修改即可继续编译；
    /// 真实客户端 <see cref="DeepSeekClient"/> 覆盖本方法，把它们真正写进 HTTP 请求体。
    /// </summary>
    /// <param name="includeModifiedRule">是否追加「旧译文仅供参考，以新英文为准」规则</param>
    /// <param name="category">本批文本分类（同一批内分类一致时才提供；混合批为 null）</param>
    Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking,
        bool includeModifiedRule,
        TextCategory? category)
        => TranslateBatchWithMetadataAsync(batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt, thinking);

    /// <summary>
    /// 第9.0B.3A轮：携带本 Run 翻译模式的批量翻译（Provider→Client→RequestComposer 全链传递 Mode）。
    /// 默认实现退化为不带 Mode 的重载，既有测试替身无需修改即可编译。
    /// </summary>
    Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking,
        bool includeModifiedRule,
        TextCategory? category,
        TranslationMode translationMode)
        => TranslateBatchWithMetadataAsync(
            batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt, thinking, includeModifiedRule, category);
}
