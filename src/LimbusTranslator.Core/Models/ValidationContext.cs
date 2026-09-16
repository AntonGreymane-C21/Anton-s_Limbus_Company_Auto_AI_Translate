namespace LimbusTranslator.Core.Models;

/// <summary>
/// 校验上下文。
///
/// Validator 只读该上下文并返回 0..N 个 <see cref="ValidationIssue"/>：
/// 禁止在 Validator 内写数据库、改 Translation Memory、调用 DeepSeek、
/// 修改游戏文件 / Diff / Manifest。这样第3轮 ReleaseGate 与第6轮 IntegrationTests 才能复用。
///
/// SourceText / Translation 必须是非 null 字符串（调用方负责统一为 string.Empty），
/// 但 Validator 仍必须自行容忍 null / empty / whitespace。
/// </summary>
public sealed class ValidationContext
{
    /// <summary>复合主键</summary>
    public required UnitKey Key { get; init; }

    /// <summary>新版英文源文（可空 → 空串语义）</summary>
    public required string SourceText { get; init; }

    /// <summary>最终译文（Placeholder 已恢复后的可见文本）</summary>
    public required string Translation { get; init; }

    /// <summary>旧版英文（可选）</summary>
    public string? OldSourceText { get; init; }

    /// <summary>旧版中文（可选）</summary>
    public string? OldTranslation { get; init; }

    /// <summary>说话人（可选）</summary>
    public string? Speaker { get; init; }

    /// <summary>译文来源（决定校验策略）</summary>
    public TranslationSource? Provenance { get; init; }

    /// <summary>术语需求（由 Pipeline 依据 GlossaryService 填充；Validator 不自行读取配置）</summary>
    public IReadOnlyList<TerminologyRequirement> Terminology { get; init; } = Array.Empty<TerminologyRequirement>();

    /// <summary>允许保留英文的合法词（HP / SP / E.G.O / UI 等 + Glossary 明确保留英文项）</summary>
    public IReadOnlySet<string> AllowedEnglishTerms { get; init; } = EmptyAllowedTerms;

    /// <summary>进入 Pipeline 之前已经发现的问题（如 Provider 的 Placeholder 宽容恢复结果）</summary>
    public IReadOnlyList<ValidationIssue> PreexistingIssues { get; init; } = Array.Empty<ValidationIssue>();

    /// <summary>
    /// 第9.0B-P1轮：本次 Run 的翻译模式（null ⇒ 旧英文语义 / EN_ONLY）。
    /// Validator 用它做**语言感知**判定，禁止靠猜源文文本。
    /// </summary>
    public TranslationMode? RunTranslationMode { get; init; }

    /// <summary>
    /// 第9.0B-P1轮：本条目实际生效的源语言（null ⇒ 英文）。
    /// 例：KR_EN 且 EN 缺失回退韩文时 = Korean。
    /// </summary>
    public SourceLanguage? EffectiveSourceLanguage { get; init; }

    /// <summary>
    /// 第9.0B-P1轮：Canonical 韩文原文是否可用。
    /// null ⇒ 不适用（EN_ONLY **永远**不产生 CANONICAL_KOREAN_SOURCE_MISSING）。
    /// </summary>
    public bool? CanonicalKoreanPresent { get; init; }

    /// <summary>
    /// 第9.0B-P1轮：本条目命中的术语（由生产计划单次匹配后注入）。
    /// 非 null 时 Pipeline 直接使用它，**不再**重新匹配术语表。
    /// </summary>
    public IReadOnlyList<TerminologyRequirement>? MatchedTerms { get; init; }

    private static readonly IReadOnlySet<string> EmptyAllowedTerms =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
