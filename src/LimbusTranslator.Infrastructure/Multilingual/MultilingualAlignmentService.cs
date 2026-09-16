using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Multilingual;

/// <summary>
/// 多语言对齐服务（第9.0轮）。
///
/// 输入：三语各自的 <see cref="TranslationUnit"/>（Key 已用逻辑路径归一化）；
/// 输出：按 <see cref="UnitKey"/> 对齐的 <see cref="MultilingualSourceBundle"/> + 缺失统计。
///
/// 规则（§10/§11）：
///   - 选择语言存在 → Primary = 选择语言，Reference = 韩文；
///   - 选择语言缺失但韩文存在 → **Fallback 到韩文** 并记录 `SELECTED_SOURCE_MISSING_FALLBACK_KO`；
///   - 韩文缺失 → 记录 `CANONICAL_KOREAN_SOURCE_MISSING`（不伪造韩文）；
///   - 三语都缺失 → 不产生绑定。
/// </summary>
public static class MultilingualAlignmentService
{
    /// <summary>选择语言缺失、回退到韩文</summary>
    public const string FallbackDiagnosticCode = "SELECTED_SOURCE_MISSING_FALLBACK_KO";

    /// <summary>韩文原文（Canonical）缺失</summary>
    public const string CanonicalMissingDiagnosticCode = "CANONICAL_KOREAN_SOURCE_MISSING";

    /// <summary>执行对齐。</summary>
    public static MultilingualAlignmentResult Align(
        IReadOnlyList<TranslationUnit>? koreanUnits,
        IReadOnlyList<TranslationUnit>? englishUnits,
        IReadOnlyList<TranslationUnit>? japaneseUnits,
        SourceLanguage selected)
    {
        var ko = ToMap(koreanUnits);
        var en = ToMap(englishUnits);
        var ja = ToMap(japaneseUnits);

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var map in new[] { ko, en, ja })
        {
            foreach (var key in map.Keys)
            {
                if (seen.Add(key))
                {
                    keys.Add(key);
                }
            }
        }

        var bundles = new List<MultilingualSourceBundle>();
        int allThree = 0, missEn = 0, missJa = 0, missKo = 0, koOnly = 0, enOnly = 0, jaOnly = 0, fallback = 0;

        foreach (var key in keys)
        {
            var hasKo = ko.TryGetValue(key, out var koUnit);
            var hasEn = en.TryGetValue(key, out var enUnit);
            var hasJa = ja.TryGetValue(key, out var jaUnit);

            if (hasKo && hasEn && hasJa)
            {
                allThree++;
            }

            if (!hasEn)
            {
                missEn++;
            }

            if (!hasJa)
            {
                missJa++;
            }

            if (!hasKo)
            {
                missKo++;
            }

            if (hasKo && !hasEn && !hasJa)
            {
                koOnly++;
            }
            else if (!hasKo && hasEn && !hasJa)
            {
                enOnly++;
            }
            else if (!hasKo && !hasEn && hasJa)
            {
                jaOnly++;
            }

            var (selectedText, selectedSpeaker, isFallback) = ResolveSelected(selected, koUnit, enUnit, jaUnit, hasKo);
            if (isFallback)
            {
                fallback++;
            }

            var diagnostics = new List<string>();
            if (isFallback)
            {
                diagnostics.Add(FallbackDiagnosticCode);
            }

            if (!hasKo)
            {
                diagnostics.Add(CanonicalMissingDiagnosticCode);
            }

            var reference = hasKo ? koUnit! : hasEn ? enUnit! : jaUnit!;

            bundles.Add(new MultilingualSourceBundle
            {
                Key = reference.Key,
                KoreanText = hasKo ? koUnit!.SourceText : null,
                EnglishText = hasEn ? enUnit!.SourceText : null,
                JapaneseText = hasJa ? jaUnit!.SourceText : null,
                KoreanSpeaker = hasKo ? koUnit!.Speaker : null,
                EnglishSpeaker = hasEn ? enUnit!.Speaker : null,
                JapaneseSpeaker = hasJa ? jaUnit!.Speaker : null,
                SelectedSourceLanguage = selected,
                SelectedSourceText = selectedText,
                SelectedSpeaker = selectedSpeaker,
                IsKoreanFallback = isFallback,
                Diagnostics = diagnostics,
            });
        }

        return new MultilingualAlignmentResult
        {
            Bundles = bundles,
            Stats = new MultilingualAlignmentStats
            {
                TotalUnits = bundles.Count,
                AllThree = allThree,
                MissingEnglish = missEn,
                MissingJapanese = missJa,
                MissingKorean = missKo,
                KoreanOnly = koOnly,
                EnglishOnly = enOnly,
                JapaneseOnly = jaOnly,
                FallbackToKorean = fallback,
            },
        };
    }

    private static (string? Text, string? Speaker, bool IsFallback) ResolveSelected(
        SourceLanguage selected,
        TranslationUnit? ko,
        TranslationUnit? en,
        TranslationUnit? ja,
        bool hasKo)
    {
        var selectedUnit = selected switch
        {
            SourceLanguage.Korean => ko,
            SourceLanguage.Japanese => ja,
            _ => en,
        };

        if (!string.IsNullOrWhiteSpace(selectedUnit?.SourceText))
        {
            return (selectedUnit!.SourceText, selectedUnit.Speaker, false);
        }

        return hasKo
            ? (ko!.SourceText, ko.Speaker, true)
            : (null, null, false);
    }

    private static Dictionary<string, TranslationUnit> ToMap(IReadOnlyList<TranslationUnit>? units)
    {
        var map = new Dictionary<string, TranslationUnit>(StringComparer.Ordinal);
        foreach (var unit in units ?? Array.Empty<TranslationUnit>())
        {
            var key = unit.Key.ToString();
            if (!map.ContainsKey(key))
            {
                map[key] = unit;
            }
        }

        return map;
    }
}