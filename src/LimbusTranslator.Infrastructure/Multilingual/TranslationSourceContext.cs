using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Multilingual;

/// <summary>
/// 翻译输入上下文（第9.0B轮）：一次翻译条目所需的「Canonical + 选择源」信息集合。
///
/// 设计取舍：不把十多个字段塞进 <c>DiffEntry</c>，而是用独立模型承载，
/// 供 Prompt / TM / Fingerprint / Trace / Validator 共同消费。
/// </summary>
public sealed class TranslationSourceContext
{
    /// <summary>单元键</summary>
    public required UnitKey Key { get; init; }

    /// <summary>用户在本次 Run 请求的源语言（配置决定，Run 内锁定）</summary>
    public required SourceLanguage RequestedLanguage { get; init; }

    /// <summary>实际使用的源语言（选择语言缺失时回退韩文）</summary>
    public required SourceLanguage EffectiveLanguage { get; init; }

    /// <summary>韩文原文（Canonical；缺失为 null，不得伪造）</summary>
    public string? CanonicalKoreanText { get; init; }

    /// <summary>旧韩文原文（Modified 时提供）</summary>
    public string? OldCanonicalKoreanText { get; init; }

    /// <summary>本次翻译依据文本</summary>
    public string? SelectedSourceText { get; init; }

    /// <summary>旧的翻译依据文本</summary>
    public string? OldSelectedSourceText { get; init; }

    /// <summary>旧中文（继承 / 参考）</summary>
    public string? OldChinese { get; init; }

    /// <summary>是否为「选择语言缺失 → 回退韩文」</summary>
    public bool IsKoreanFallback { get; init; }

    /// <summary>韩文原文（Canonical）是否缺失</summary>
    public bool CanonicalKoreanMissing { get; init; }

    /// <summary>Canonical（韩文）是否发生变化</summary>
    public bool CanonicalChanged { get; init; }

    /// <summary>英文参考是否变化</summary>
    public bool EnglishChanged { get; init; }

    /// <summary>日文参考是否变化</summary>
    public bool JapaneseChanged { get; init; }

    /// <summary>请求语言与有效语言是否不一致（诊断）</summary>
    public bool UsedFallback => RequestedLanguage != EffectiveLanguage;

    /// <summary>韩文是否即为主翻译依据（KO 模式或回退）</summary>
    public bool KoreanIsPrimary => EffectiveLanguage == SourceLanguage.Korean;
}

/// <summary>
/// 翻译输入上下文构造器（第9.0B轮）：处理 Unit 级缺失与 Fallback（Requested vs Effective 不混淆）。
/// </summary>
public static class TranslationSourceContextBuilder
{
    /// <summary>构造上下文。</summary>
    public static TranslationSourceContext Build(
        UnitKey key,
        SourceLanguage requested,
        string? koreanText,
        string? englishText,
        string? japaneseText,
        string? oldKoreanText = null,
        string? oldEnglishText = null,
        string? oldJapaneseText = null,
        string? oldChinese = null)
    {
        ArgumentNullException.ThrowIfNull(key);

        var selected = requested switch
        {
            SourceLanguage.Korean => koreanText,
            SourceLanguage.Japanese => japaneseText,
            _ => englishText,
        };

        string? oldSelected = requested switch
        {
            SourceLanguage.Korean => oldKoreanText,
            SourceLanguage.Japanese => oldJapaneseText,
            _ => oldEnglishText,
        };

        var hasKorean = !string.IsNullOrWhiteSpace(koreanText);
        var effective = requested;
        var isFallback = false;

        if (string.IsNullOrWhiteSpace(selected))
        {
            if (hasKorean)
            {
                // 选择语言缺失 → 回退韩文（Effective=Korean，Requested 不变）
                selected = koreanText;
                oldSelected = oldKoreanText;
                effective = SourceLanguage.Korean;
                isFallback = true;
            }
        }

        return new TranslationSourceContext
        {
            Key = key,
            RequestedLanguage = requested,
            EffectiveLanguage = effective,
            CanonicalKoreanText = hasKorean ? koreanText : null,
            OldCanonicalKoreanText = string.IsNullOrWhiteSpace(oldKoreanText) ? null : oldKoreanText,
            SelectedSourceText = selected,
            OldSelectedSourceText = string.IsNullOrWhiteSpace(oldSelected) ? null : oldSelected,
            OldChinese = string.IsNullOrWhiteSpace(oldChinese) ? null : oldChinese,
            IsKoreanFallback = isFallback,
            CanonicalKoreanMissing = !hasKorean,
            CanonicalChanged = !string.Equals(koreanText, oldKoreanText, StringComparison.Ordinal),
            EnglishChanged = !string.Equals(englishText, oldEnglishText, StringComparison.Ordinal),
            JapaneseChanged = !string.Equals(japaneseText, oldJapaneseText, StringComparison.Ordinal),
        };
    }
}