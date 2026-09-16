namespace LimbusTranslator.Core.Release;

/// <summary>
/// 发布门禁状态。
///
/// 语义：
///   Passed               无 Error / 无 Warning / 无待审核 → 可以部署
///   RequiresConfirmation 无阻断性 Error，但存在 Warning 或待审核（或历史继承结构差异）→ 必须人工确认
///   Blocked              存在阻断性 Error → 永远禁止部署（不可人工绕过）
/// </summary>
public enum ReleaseGateStatus
{
    /// <summary>通过</summary>
    Passed,

    /// <summary>需要人工确认</summary>
    RequiresConfirmation,

    /// <summary>阻断（禁止部署）</summary>
    Blocked,
}

/// <summary>
/// 门禁问题分类（稳定机器码，供 Manifest / WPF / 未来 CLI 引用）。
/// </summary>
public static class ReleaseGateReasonKinds
{
    /// <summary>新译文（AI / Mock / TM / 请求缓存）存在硬安全 Error</summary>
    public const string NewTranslationHardError = "NEW_TRANSLATION_HARD_ERROR";

    /// <summary>人工 / 官方译文存在硬安全 Error</summary>
    public const string ReviewedHardError = "REVIEWED_HARD_ERROR";

    /// <summary>历史继承旧中文存在结构安全差异（Validator 上线前已存在）</summary>
    public const string HistoricalInheritedError = "HISTORICAL_INHERITED_ERROR";

    /// <summary>新译文存在启发式 Warning</summary>
    public const string NewTranslationWarning = "NEW_TRANSLATION_WARNING";

    /// <summary>历史继承旧中文的启发式 Warning（仅统计，不阻断）</summary>
    public const string HistoricalInheritedWarning = "HISTORICAL_INHERITED_WARNING";

    /// <summary>人工 / 官方译文存在启发式 Warning（仅统计，不影响人工确认结论）</summary>
    public const string ReviewedWarning = "REVIEWED_WARNING";

    /// <summary>存在待审核条目</summary>
    public const string NeedsReview = "NEEDS_REVIEW";

    /// <summary>清单缺少可验证的门禁信息（旧版清单）</summary>
    public const string LegacyManifest = "LEGACY_MANIFEST";

    /// <summary>权威结构里存在、但最终 output 里缺失的 Key（第9.0B-P4轮）</summary>
    public const string MissingExpectedKey = "MISSING_EXPECTED_KEY";

    /// <summary>最终 output 里出现、但权威结构里不存在的 Key（第9.0B-P4轮；例如英文残留的旧 Key）</summary>
    public const string UnexpectedOutputKey = "UNEXPECTED_OUTPUT_KEY";
}
