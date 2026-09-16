namespace LimbusTranslator.Infrastructure.Diagnostics;

/// <summary>
/// 翻译 Trace 条目（第4轮）。
///
/// Trace 是**审计 / 诊断信息**，不是 Translation Memory，也不是缓存本体。
///
/// 脱敏规则（强制）：
///   禁止写入 API Key / Authorization / Cookie / Secret；
///   默认不写入完整 Prompt / 完整 Response / 完整 SourceText / 完整 Translation；
///   只保留 Hash + 元数据 + 清洗后的错误摘要。
/// </summary>
public sealed class TranslationTraceEntry
{
    /// <summary>Trace 格式版本</summary>
    public int TraceFormatVersion { get; init; } = 1;

    /// <summary>记录时间（UTC）</summary>
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>一次翻译运行（WPF/CLI 一次运行）内稳定不变</summary>
    public required string RunId { get; init; }

    /// <summary>逻辑请求 Id（同一次请求的多次重试共享）</summary>
    public required string RequestId { get; init; }

    /// <summary>Stage（文件）标识</summary>
    public string? StageId { get; init; }

    /// <summary>Batch 标识</summary>
    public string? BatchId { get; init; }

    /// <summary>本请求涉及的 UnitKey 列表（不强行压缩成一个）</summary>
    public IReadOnlyList<string> UnitKeys { get; init; } = Array.Empty<string>();

    /// <summary>条目数</summary>
    public int ItemCount { get; init; }

    /// <summary>Provider 标识</summary>
    public string? Provider { get; init; }

    /// <summary>模型名</summary>
    public string? Model { get; init; }

    /// <summary>请求指纹</summary>
    public string? Fingerprint { get; init; }

    /// <summary>系统提示词内容 Hash</summary>
    public string? PromptHash { get; init; }

    /// <summary>上下文 Hash</summary>
    public string? ContextHash { get; init; }

    /// <summary>术语子集 Hash</summary>
    public string? GlossarySubsetHash { get; init; }

    /// <summary>角色风格 Hash</summary>
    public string? CharacterStyleHash { get; init; }

    /// <summary>是否命中 request_cache</summary>
    public bool CacheHit { get; init; }

    /// <summary>是否调用了 Provider / 网络</summary>
    public bool NetworkCalled { get; init; }

    /// <summary>重试次数</summary>
    public int RetryCount { get; init; }

    /// <summary>耗时（毫秒）</summary>
    public long DurationMs { get; init; }

    /// <summary>响应 Id（Provider 返回时）</summary>
    public string? ResponseId { get; init; }

    /// <summary>响应模型名（Provider 返回时）</summary>
    public string? ResponseModel { get; init; }

    /// <summary>输入 token</summary>
    public int? InputTokens { get; init; }

    /// <summary>输出 token</summary>
    public int? OutputTokens { get; init; }

    /// <summary>总 token</summary>
    public int? TotalTokens { get; init; }

    /// <summary>
    /// 本次请求是否开启 Thinking（第8.75轮，自适应策略下逐批决定）。
    /// </summary>
    public bool? ThinkingEnabled { get; init; }

    /// <summary>
    /// Thinking 决策原因（稳定码：SourceLanguageAnomaly / StoryData / DefaultOff / AlwaysOn / AlwaysOff）。
    /// 只记原因码，不记录任何源文内容。
    /// </summary>
    public string? ThinkingPolicyReason { get; init; }

    /// <summary>
    /// 隐藏推理 token（第8.5轮，thinking 模式；缺失为 null）。
    /// </summary>
    public int? ReasoningTokens { get; init; }

    /// <summary>
    /// 模型真正输出的可见译文 token = OutputTokens - ReasoningTokens（任一缺失为 null）。
    /// 用于区分「可见译文成本」与「隐藏推理成本」。
    /// </summary>
    public int? VisibleOutputTokens { get; init; }

    /// <summary>
    /// 第8.87轮：本批命中的术语摘要（<c>Philip→菲利普[L]|…</c>，最多 20 条）。
    /// 用于诊断「术语表是否真的注入到 Prompt」，不包含任何源文内容。
    /// </summary>
    public string? GlossaryTerms { get; init; }

    /// <summary>
    /// 本次运行使用的术语快照 Hash（第8.875轮；仅用于诊断，不进入请求指纹）。
    /// </summary>
    public string? GlossarySnapshotHash { get; init; }

    /// <summary>本次请求是否成功</summary>
    public bool Success { get; init; }

    // ───────── 第9.0B-P1轮：四模式语义字段 ─────────

    /// <summary>翻译模式配置码（en_only / kr_en / kr_jp / kr_only）</summary>
    public string? TranslationMode { get; init; }

    /// <summary>本请求实际生效的源语言（en / ko / ja）</summary>
    public string? EffectiveSourceLanguage { get; init; }

    /// <summary>是否因选择的参考译本缺失而回退韩文</summary>
    public bool? UsedKoreanFallback { get; init; }

    /// <summary>
    /// 本请求是否携带韩文 Canonical 原文。
    /// **EN_ONLY 必须为 null（= N/A）**，不得写 false —— EN_ONLY 根本不使用 Canonical。
    /// </summary>
    public bool? CanonicalKoreanPresent { get; init; }

    /// <summary>韩文 Canonical 原文是否发生变化（**EN_ONLY 必须为 null = N/A**）</summary>
    public bool? CanonicalChanged { get; init; }

    /// <summary>英文参考译本是否变化（仅诊断，不影响动作/缓存语义）</summary>
    public bool? EnglishChanged { get; init; }

    /// <summary>日文参考译本是否变化（仅诊断，不影响动作/缓存语义）</summary>
    public bool? JapaneseChanged { get; init; }

    /// <summary>
    /// 第9.0C.2轮：请求种类（<c>locked_terminology_repair</c> = 锁定术语修正请求；null = 常规翻译）。
    /// </summary>
    public string? RequestKind { get; init; }

    /// <summary>清洗后的错误摘要（异常类型 / HTTP 状态 / 简短原因）</summary>
    public string? ErrorSummary { get; init; }
}
