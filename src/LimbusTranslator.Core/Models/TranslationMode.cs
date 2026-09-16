namespace LimbusTranslator.Core.Models;

/// <summary>
/// 翻译模式（第9.0B.1轮）：系统的**一级概念**，决定 Diff 来源、Prompt 结构、TM/Cache 隔离与 Validator 语义。
///
/// 四种模式互相独立：
///   <see cref="EnglishOnly"/>   纯英文 → 简中（**继续使用旧 EN Diff**，不受 KR Canonical 影响）
///   <see cref="KoreanEnglish"/> 韩文原文 + 英文译本 → 简中（KR Canonical Diff）
///   <see cref="KoreanJapanese"/>韩文原文 + 日文译本 → 简中（KR Canonical Diff）
///   <see cref="KoreanOnly"/>    纯韩文原文 → 简中（KR Canonical Diff）
/// </summary>
public enum TranslationMode
{
    /// <summary>纯英文（旧工作流，兼容旧 TM）</summary>
    EnglishOnly = 0,

    /// <summary>韩文 + 英文</summary>
    KoreanEnglish = 1,

    /// <summary>韩文 + 日文</summary>
    KoreanJapanese = 2,

    /// <summary>纯韩文</summary>
    KoreanOnly = 3,
}

/// <summary>翻译模式配置码 / 显示名 / 解析（Fail-closed 由调用方负责）。</summary>
public static class TranslationModeCodes
{
    /// <summary>纯英文</summary>
    public const string EnglishOnly = "en_only";

    /// <summary>韩文 + 英文</summary>
    public const string KoreanEnglish = "kr_en";

    /// <summary>韩文 + 日文</summary>
    public const string KoreanJapanese = "kr_jp";

    /// <summary>纯韩文</summary>
    public const string KoreanOnly = "kr_only";

    /// <summary>默认模式（保证旧用户升级后行为不变）</summary>
    public const TranslationMode Default = TranslationMode.EnglishOnly;

    /// <summary>全部模式（固定顺序）</summary>
    public static IReadOnlyList<TranslationMode> All { get; } = new[]
    {
        TranslationMode.EnglishOnly,
        TranslationMode.KoreanEnglish,
        TranslationMode.KoreanJapanese,
        TranslationMode.KoreanOnly,
    };

    /// <summary>配置码。</summary>
    public static string ToCode(TranslationMode mode) => mode switch
    {
        TranslationMode.KoreanEnglish => KoreanEnglish,
        TranslationMode.KoreanJapanese => KoreanJapanese,
        TranslationMode.KoreanOnly => KoreanOnly,
        _ => EnglishOnly,
    };

    /// <summary>显示名。</summary>
    public static string GetDisplayName(TranslationMode mode) => mode switch
    {
        TranslationMode.KoreanEnglish => "韩文 + 英文",
        TranslationMode.KoreanJapanese => "韩文 + 日文",
        TranslationMode.KoreanOnly => "纯韩文",
        _ => "纯英文",
    };

    /// <summary>解析配置码（无法识别 → null，由调用方 Fail-closed）。</summary>
    public static TranslationMode? TryParseCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim().ToLowerInvariant() switch
        {
            "en_only" or "en-only" or "enonly" => TranslationMode.EnglishOnly,
            "kr_en" or "kr-en" => TranslationMode.KoreanEnglish,
            "kr_jp" or "kr-jp" => TranslationMode.KoreanJapanese,
            "kr_only" or "kr-only" or "kronly" => TranslationMode.KoreanOnly,
            _ => null,
        };
    }

    /// <summary>
    /// 旧配置 <c>translationSourceLanguage</c> → 模式的迁移解释。
    /// **旧 en 必须解释为 EN_ONLY（不得解释为 KR_EN）**；旧 ko → 纯韩文；旧 ja → 韩文 + 日文。
    /// </summary>
    public static TranslationMode FromLegacySourceLanguage(SourceLanguage? legacy) => legacy switch
    {
        SourceLanguage.Korean => TranslationMode.KoreanOnly,
        SourceLanguage.Japanese => TranslationMode.KoreanJapanese,
        _ => TranslationMode.EnglishOnly,
    };
}