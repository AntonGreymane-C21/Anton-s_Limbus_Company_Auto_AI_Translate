namespace LimbusTranslator.Core.Models;

/// <summary>
/// 翻译模式策略（第9.0B.1轮）：各层（Diff / Prompt / Glossary / TM / Validator / Thinking）行为的唯一事实来源。
/// </summary>
public static class TranslationModePolicy
{
    /// <summary>是否使用韩文 Canonical Diff（EN_ONLY 必须继续使用旧 EN Diff）。</summary>
    public static bool UsesCanonicalKoreanDiff(TranslationMode mode) => mode != TranslationMode.EnglishOnly;

    /// <summary>
    /// 输出结构是否以**韩文**为权威（第9.0B-P4轮）：
    ///   EN_ONLY → false（输出文件结构 / Expected Key 集以当前英文为准，保持历史兼容）；
    ///   KR_EN / KR_JP / KR_ONLY → true（输出结构与 Expected Key 集以当前韩文为准，EN/JP 仅作参考译本）。
    /// 这是「输出结构权威」的**唯一定义处**：Merge / ReleaseGate / WPF / CLI 都必须引用它。
    /// </summary>
    public static bool UsesCanonicalKoreanOutput(TranslationMode mode) => mode != TranslationMode.EnglishOnly;

    /// <summary>输出结构权威来源语言（见 <see cref="UsesCanonicalKoreanOutput"/>）。</summary>
    public static SourceLanguage GetOutputAuthoritativeLanguage(TranslationMode mode)
        => UsesCanonicalKoreanOutput(mode) ? SourceLanguage.Korean : SourceLanguage.English;

    /// <summary>是否使用首次 Canonical Baseline 迁移模式（仅三个 KR 模式）。</summary>
    public static bool UsesBaselineMigration(TranslationMode mode) => mode != TranslationMode.EnglishOnly;

    /// <summary>参考译本语言（EN_ONLY / KR_EN → 英文；KR_JP → 日文；KR_ONLY → 无）。</summary>
    public static SourceLanguage? GetReferenceLanguage(TranslationMode mode) => mode switch
    {
        TranslationMode.EnglishOnly or TranslationMode.KoreanEnglish => SourceLanguage.English,
        TranslationMode.KoreanJapanese => SourceLanguage.Japanese,
        _ => null,
    };

    /// <summary>Prompt 是否加入「韩文权威高于译本」规则（仅 KR_EN / KR_JP）。</summary>
    public static bool IncludesKoreanAuthorityRule(TranslationMode mode)
        => mode is TranslationMode.KoreanEnglish or TranslationMode.KoreanJapanese;

    /// <summary>Prompt 是否发送韩文区块（三个 KR 模式；EN_ONLY **绝不**发送）。</summary>
    public static bool SendsKorean(TranslationMode mode) => mode != TranslationMode.EnglishOnly;

    /// <summary>术语匹配源（KR_EN → KR+EN；KR_JP → KR+JP；KR_ONLY → KR；EN_ONLY → EN）。</summary>
    public static IReadOnlyList<SourceLanguage> GetGlossarySources(TranslationMode mode) => mode switch
    {
        TranslationMode.KoreanEnglish => new[] { SourceLanguage.Korean, SourceLanguage.English },
        TranslationMode.KoreanJapanese => new[] { SourceLanguage.Korean, SourceLanguage.Japanese },
        TranslationMode.KoreanOnly => new[] { SourceLanguage.Korean },
        _ => new[] { SourceLanguage.English },
    };

    /// <summary>
    /// 第9.0B 最终轮：**邻句上下文来源语言**（与 Selected Source 一致）：
    ///   EN_ONLY → 英文；KR_EN → 英文（参考译本）；KR_JP → 日文（参考译本）；KR_ONLY → 韩文（原文）。
    ///
    /// 目的：KR_ONLY 的真实请求不得出现英文/日文邻句；KR_JP 的邻接参考应与它实际翻译的日文一致。
    /// 这是「邻句来源」的**唯一定义处**：CLI / WPF / 测试都必须引用它。
    /// </summary>
    public static SourceLanguage GetNeighborSourceLanguage(TranslationMode mode) => mode switch
    {
        TranslationMode.KoreanJapanese => SourceLanguage.Japanese,
        TranslationMode.KoreanOnly => SourceLanguage.Korean,
        _ => SourceLanguage.English,
    };

    /// <summary>没有韩文即无法翻译（KR_ONLY：不得调用 Provider）。</summary>
    public static bool RequiresKoreanSource(TranslationMode mode) => mode == TranslationMode.KoreanOnly;

    /// <summary>是否允许「参考译本缺失 → 回退韩文」（EN_ONLY **不允许**）。</summary>
    public static bool AllowsKoreanFallback(TranslationMode mode)
        => mode is TranslationMode.KoreanEnglish or TranslationMode.KoreanJapanese;

    /// <summary>是否应产生 CANONICAL_KOREAN_SOURCE_MISSING（仅三个 KR 模式）。</summary>
    public static bool AppliesCanonicalMissingWarning(TranslationMode mode) => mode != TranslationMode.EnglishOnly;

    /// <summary>EN_ONLY：Trace 中 canonical 字段标为 not-applicable（不假装使用了 KR）。</summary>
    public static bool CanonicalFieldsNotApplicable(TranslationMode mode) => mode == TranslationMode.EnglishOnly;

    /// <summary>本模式是否应 ON Thinking（最小规则：KR_ONLY 恒 ON；KR 模式回退韩文时 ON）。</summary>
    public static bool ShouldForceThinking(TranslationMode mode, SourceLanguage effective)
        => mode == TranslationMode.KoreanOnly
           || (TranslationModePolicy.UsesCanonicalKoreanDiff(mode) && effective == SourceLanguage.Korean);

    /// <summary>
    /// TM SourceHash 盐（v3）：
    ///   EN_ONLY → <c>null</c>（**与历史 EN 哈希逐字节相同**，旧 TM 继续命中）；
    ///   其它模式 → <c>v3|mode={code}|effective={lang}|ko={hash16|-}</c>（四模式含 Fallback 互不命中）。
    /// </summary>
    public static string? BuildModeSalt(TranslationMode mode, SourceLanguage effective, string? canonicalKoreanText)
    {
        if (mode == TranslationMode.EnglishOnly)
        {
            return null;
        }

        var koreanHash = string.IsNullOrWhiteSpace(canonicalKoreanText)
            ? "-"
            : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(canonicalKoreanText!)))[..16];

        return $"v3|mode={TranslationModeCodes.ToCode(mode)}|effective={SourceLanguageHelper.ToCode(effective)}|ko={koreanHash}";
    }
}