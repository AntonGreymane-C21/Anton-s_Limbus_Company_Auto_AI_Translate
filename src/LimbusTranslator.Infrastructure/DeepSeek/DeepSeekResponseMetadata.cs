namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// DeepSeek（OpenAI 兼容）响应元数据（仅用于 Trace / 诊断）。
/// </summary>
public sealed record DeepSeekResponseMetadata(
    string? ResponseId,
    string? ResponseModel,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    int? ReasoningTokens = null)
{
    /// <summary>
    /// 模型真正输出的可见译文 token（第8.5轮）。
    /// = CompletionTokens - ReasoningTokens；任一为 null 时为 null（缺失不得导致失败）。
    /// </summary>
    public int? VisibleOutputTokens =>
        CompletionTokens.HasValue && ReasoningTokens.HasValue
            ? Math.Max(0, CompletionTokens.Value - ReasoningTokens.Value)
            : null;
}
