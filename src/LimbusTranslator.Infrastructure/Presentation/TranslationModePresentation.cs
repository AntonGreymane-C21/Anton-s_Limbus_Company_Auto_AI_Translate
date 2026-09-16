using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>
/// 翻译模式的**用户可见文案**（第9.0C轮，GUI 产品化）。
///
/// 约束：
///   - 只做「枚举 → 中文文案」的映射，**不改变任何模式语义**（语义唯一来源仍是
///     <see cref="TranslationModePolicy"/>）；
///   - 默认值仍尊重配置文件（`translationMode`），GUI 只提供选择入口与「推荐」标签；
///   - 不写「最佳 / 最高质量」这类结论性描述。
/// </summary>
public sealed record TranslationModeOption(
    TranslationMode Mode,
    string DisplayName,
    string Subtitle,
    string Description,
    bool IsRecommended,
    string InternalCode)
{
    /// <summary>下拉框/卡片上的一行摘要（名称 + 推荐标签）。</summary>
    public string Headline => IsRecommended ? $"{DisplayName}（推荐）" : DisplayName;

    public override string ToString() => Headline;
}

/// <summary>模式选择器的文案表（唯一实现，WPF / 测试共用）。</summary>
public static class TranslationModePresentation
{
    /// <summary>四种模式（顺序固定：EN_ONLY → KR_EN → KR_JP → KR_ONLY）。</summary>
    public static IReadOnlyList<TranslationModeOption> All { get; } = new[]
    {
        new TranslationModeOption(
            TranslationMode.EnglishOnly,
            "英文翻译",
            "仅使用官方英文文本",
            "只使用官方英文文本进行翻译。兼容旧版英文翻译流程，速度和 Token 消耗通常最低。",
            IsRecommended: false,
            TranslationModeCodes.ToCode(TranslationMode.EnglishOnly)),
        new TranslationModeOption(
            TranslationMode.KoreanEnglish,
            "韩文原文 + 英文参考",
            "以韩文原文为准，英文作为辅助参考",
            "以韩文原文作为最终语义依据，同时参考官方英文文本。兼顾韩文原意和英文参考文本。",
            IsRecommended: true,
            TranslationModeCodes.ToCode(TranslationMode.KoreanEnglish)),
        new TranslationModeOption(
            TranslationMode.KoreanJapanese,
            "韩文原文 + 日文参考",
            "以韩文原文为准，日文作为辅助参考",
            "以韩文原文作为最终语义依据，同时参考官方日文文本。",
            IsRecommended: false,
            TranslationModeCodes.ToCode(TranslationMode.KoreanJapanese)),
        new TranslationModeOption(
            TranslationMode.KoreanOnly,
            "仅韩文原文",
            "直接根据韩文原文翻译",
            "仅根据韩文原文翻译，不会参考英文或日文。通常会启用更强的推理，Token 消耗可能更高。",
            IsRecommended: false,
            TranslationModeCodes.ToCode(TranslationMode.KoreanOnly)),
    };

    /// <summary>按枚举取文案（未知值按 EN_ONLY 处理，保证 GUI 永不空白）。</summary>
    public static TranslationModeOption Resolve(TranslationMode mode)
        => All.FirstOrDefault(option => option.Mode == mode) ?? All[0];

    /// <summary>状态栏用的短名称（如「韩文 + 英文」）。</summary>
    public static string ShortName(TranslationMode mode) => mode switch
    {
        TranslationMode.EnglishOnly => "英文",
        TranslationMode.KoreanEnglish => "韩文 + 英文",
        TranslationMode.KoreanJapanese => "韩文 + 日文",
        TranslationMode.KoreanOnly => "仅韩文",
        _ => "英文",
    };
}
