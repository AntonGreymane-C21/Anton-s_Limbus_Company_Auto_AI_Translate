using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Multilingual;

/// <summary>
/// 多语言源文绑定（第9.0轮）。
///
/// 语义：
///   - <see cref="KoreanText"/> = 韩文原文（Canonical，权威；缺失为 null，**不得伪造**）；
///   - <see cref="SelectedSourceText"/> = 本次翻译依据文本（用户选择语言，已应用 Fallback）；
///   - <see cref="SelectedSourceLanguage"/> = 本次 Run 固定的语言模式。
///
/// 设计取舍：不把三语字段直接塞进 <c>TranslationUnit</c>，避免污染既有 TM / Coordinator / Provider 链路。
/// </summary>
public sealed class MultilingualSourceBundle
{
    /// <summary>单元键（三语共用；用逻辑路径对齐）</summary>
    public required UnitKey Key { get; init; }

    /// <summary>韩文原文（Canonical；缺失为 null）</summary>
    public string? KoreanText { get; init; }

    /// <summary>英文译本（缺失为 null）</summary>
    public string? EnglishText { get; init; }

    /// <summary>日文译本（缺失为 null）</summary>
    public string? JapaneseText { get; init; }

    /// <summary>韩文 Speaker（canonical）</summary>
    public string? KoreanSpeaker { get; init; }

    /// <summary>英文 Speaker</summary>
    public string? EnglishSpeaker { get; init; }

    /// <summary>日文 Speaker</summary>
    public string? JapaneseSpeaker { get; init; }

    /// <summary>本次 Run 的翻译依据语言</summary>
    public required SourceLanguage SelectedSourceLanguage { get; init; }

    /// <summary>本次翻译依据文本（已应用 Fallback）</summary>
    public string? SelectedSourceText { get; init; }

    /// <summary>本次翻译依据的 Speaker</summary>
    public string? SelectedSpeaker { get; init; }

    /// <summary>是否存在韩文原文</summary>
    public bool HasKorean => !string.IsNullOrWhiteSpace(KoreanText);

    /// <summary>是否为「选择语言缺失 → Fallback 到韩文」</summary>
    public bool IsKoreanFallback { get; init; }

    /// <summary>韩文原文缺失（不得伪造；需 NeedsReview + 诊断）</summary>
    public bool CanonicalKoreanMissing => !HasKorean;

    /// <summary>诊断码（稳定机器码）</summary>
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>多语言对齐统计（GUI 简要展示）。</summary>
public sealed class MultilingualAlignmentStats
{
    /// <summary>Canonical 单元总数</summary>
    public int TotalUnits { get; init; }

    /// <summary>三语全有</summary>
    public int AllThree { get; init; }

    /// <summary>缺英文</summary>
    public int MissingEnglish { get; init; }

    /// <summary>缺日文</summary>
    public int MissingJapanese { get; init; }

    /// <summary>缺韩文</summary>
    public int MissingKorean { get; init; }

    /// <summary>仅韩文</summary>
    public int KoreanOnly { get; init; }

    /// <summary>仅英文</summary>
    public int EnglishOnly { get; init; }

    /// <summary>仅日文</summary>
    public int JapaneseOnly { get; init; }

    /// <summary>选择语言缺失 → Fallback 到韩文的数量</summary>
    public int FallbackToKorean { get; init; }

    /// <summary>一行式摘要</summary>
    public string Describe() =>
        $"Canonical 单元={TotalUnits}｜三语全有={AllThree}｜缺EN={MissingEnglish}｜缺JP={MissingJapanese}｜缺KR={MissingKorean}" +
        $"｜KR-only={KoreanOnly}｜EN-only={EnglishOnly}｜JP-only={JapaneseOnly}｜Fallback→KR={FallbackToKorean}";
}

/// <summary>多语言对齐结果。</summary>
public sealed class MultilingualAlignmentResult
{
    /// <summary>对齐后的绑定（仅保留至少有一种文本的单元）</summary>
    public required IReadOnlyList<MultilingualSourceBundle> Bundles { get; init; }

    /// <summary>统计</summary>
    public required MultilingualAlignmentStats Stats { get; init; }
}