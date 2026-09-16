namespace LimbusTranslator.Core.Models;

/// <summary>
/// 翻译结果。
/// </summary>
public sealed class TranslationResult
{
    /// <summary>复合主键</summary>
    public required UnitKey Key { get; init; }

    /// <summary>译文</summary>
    public required string Translation { get; init; }

    /// <summary>翻译来源</summary>
    public TranslationSource Source { get; init; } = TranslationSource.AI;

    /// <summary>是否需要人工审核</summary>
    public bool NeedsReview { get; init; }

    /// <summary>审核原因</summary>
    public string? ReviewReason { get; init; }

    /// <summary>Translation Memory 命中级别（非 TM 来源时为 None）</summary>
    public TranslationMemoryMatchType TmMatchType { get; init; } = TranslationMemoryMatchType.None;

    /// <summary>
    /// 第2轮：Provider 侧已发现的结构化问题（如 Placeholder 宽容恢复说明）。
    /// Pipeline 会把这些 Issue 合并进 ValidationReport。
    /// </summary>
    public IReadOnlyList<ValidationIssue> Issues { get; init; } = Array.Empty<ValidationIssue>();

    // ---------- 第4轮：Request Cache / Trace ----------

    /// <summary>产生该译文的 Provider 请求指纹（Cache 命中时与写入时相同；无缓存时可为 null）</summary>
    public string? RequestFingerprint { get; init; }

    /// <summary>该结果是否来自 request_cache 命中（false 表示调用了 Provider/网络）</summary>
    public bool CacheHit { get; init; }

    /// <summary>逻辑请求 Id（同一次请求的多次重试共享；用于 Trace 关联）</summary>
    public string? RequestId { get; init; }
}

/// <summary>
/// 译文来源优先级：Official > HumanReviewed > Imported > AI
///
/// 【兼容性约束】既有数值（0=Official / 1=HumanReviewed / 2=Imported / 3=AI）
/// 已写入 SQLite，禁止改动；新来源只能追加在末尾，
/// 使既有数据库记录语义保持不变，且 ASC 排序仍等于优先级排序。
/// </summary>
public enum TranslationSource
{
    /// <summary>官方译文</summary>
    Official,

    /// <summary>人工审核过</summary>
    HumanReviewed,

    /// <summary>导入的历史译文</summary>
    Imported,

    /// <summary>AI 生成</summary>
    AI,

    /// <summary>直接继承旧中文（运行期来源，不写回 Translation Memory）</summary>
    Inherited,

    /// <summary>来自 Translation Memory 命中（运行期来源，不重复写回）</summary>
    TranslationMemory,

    /// <summary>模拟翻译（无 API Key 时的开发 / 演示来源；不得冒充 AI）</summary>
    Mock,

    /// <summary>来自请求缓存（request_cache；第 4 轮接线前不会产生）</summary>
    RequestCache,

    /// <summary>
    /// 系统原样保留（第8.5轮）：纯 Symbol / Punctuation 文本（无 Unicode 字母与数字）不调用 AI，
    /// 直接把 SourceText 作为译文。属于确定性系统行为，**不写回 Translation Memory**。
    /// </summary>
    Passthrough,
}
