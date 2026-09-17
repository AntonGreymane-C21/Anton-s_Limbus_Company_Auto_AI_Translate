using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Review;

/// <summary>
/// 待审核筛选类型（第8.85轮）。
///
/// 只做 UI 投影：判据全部来自条目**已有**的 <see cref="DiffEntry.ValidationIssues"/> /
/// <see cref="DiffEntry.NeedsReview"/>，**不重新运行 Validator**。
/// </summary>
public enum ReviewFilterKind
{
    All = 0,
    NeedsReview,
    Error,
    Warning,
    SourceLanguageAnomaly,
    TerminologyMismatch,
    KoreanResidue,
    EnglishResidue,
    PlaceholderMismatch,
    TagMismatch,

    // ── 第8.88轮：逐条审校常用筛选 ──
    /// <summary>本次 AI 翻译（来源=AI 且非继承/直通）</summary>
    AiTranslated,

    /// <summary>修改过的文本（TranslateModified）</summary>
    Modified,

    /// <summary>StoryData 分类</summary>
    StoryData,

    /// <summary>韩文异常源（源文含韩文）</summary>
    KoreanSource,

    /// <summary>纯符号直通（默认不需人工审校）</summary>
    Passthrough,

    /// <summary>默认工作集：本次 AI + NeedsReview（排除直通/继承）</summary>
    DefaultWorkSet,

    // ── 第9.0C.14轮：按"问题类型"一键筛选（用户反馈：想集中修某一类问题） ──
    /// <summary>译文中仍有日文假名残留</summary>
    JapaneseResidue,

    /// <summary>译文为空</summary>
    EmptyTranslation,

    /// <summary>缺少韩文原文（仅 KR 三模式）</summary>
    CanonicalKoreanMissing,
}

/// <summary>
/// 待审核筛选（纯函数，便于单元测试；不触碰数据库、不运行 Validator）。
/// </summary>
public static class ReviewFilter
{
    /// <summary>筛选下拉的显示文本（顺序与 <see cref="ReviewFilterKind"/> 一致）。</summary>
    public static IReadOnlyList<string> DisplayNames { get; } = new[]
    {
        "全部",
        "只看 NeedsReview",
        "只看 Error",
        "只看 Warning",
        "只看 SOURCE_LANGUAGE_ANOMALY",
        "只看 TERMINOLOGY_MISMATCH",
        "只看 KOREAN_RESIDUE",
        "只看 ENGLISH_RESIDUE",
        "只看 PLACEHOLDER_MISMATCH",
        "只看 TAG_MISMATCH",
        "本次 AI 翻译",
        "只看 Modified",
        "只看 StoryData",
        "韩文异常源",
        "只看 Passthrough",
        "默认工作集（AI + NeedsReview）",
        // 第9.0C.14轮：按问题类型（顺序必须与枚举一致）
        "只看 JAPANESE_RESIDUE（日文假名残留）",
        "只看 EMPTY_TRANSLATION（空译文）",
        "只看 CANONICAL_KOREAN_SOURCE_MISSING（缺韩文原文）",
    };

    /// <summary>显示文本 → 筛选类型（无法识别时按"全部"处理）。</summary>
    public static ReviewFilterKind Parse(string? displayName)
    {
        var index = DisplayNames
            .Select((name, i) => (name, i))
            .FirstOrDefault(x => string.Equals(x.name, displayName, StringComparison.Ordinal))
            .i;
        return displayName is not null && DisplayNames.Contains(displayName)
            ? (ReviewFilterKind)index
            : ReviewFilterKind.All;
    }

    /// <summary>条目是否满足筛选条件。</summary>
    public static bool Matches(DiffEntry entry, ReviewFilterKind kind)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return kind switch
        {
            ReviewFilterKind.All => true,
            ReviewFilterKind.NeedsReview => entry.NeedsReview,
            ReviewFilterKind.Error => HasSeverity(entry, ValidationSeverity.Error),
            ReviewFilterKind.Warning => HasSeverity(entry, ValidationSeverity.Warning),
            ReviewFilterKind.SourceLanguageAnomaly => HasCode(entry, ValidationIssueCodes.SourceLanguageAnomaly),
            ReviewFilterKind.TerminologyMismatch => HasCode(entry, ValidationIssueCodes.TerminologyMismatch),
            ReviewFilterKind.KoreanResidue => HasCode(entry, ValidationIssueCodes.KoreanResidue),
            ReviewFilterKind.EnglishResidue => HasCode(entry, ValidationIssueCodes.EnglishResidue),
            ReviewFilterKind.PlaceholderMismatch => HasCode(entry, ValidationIssueCodes.PlaceholderMismatch),
            ReviewFilterKind.TagMismatch => HasCode(entry, ValidationIssueCodes.TagMismatch),
            // 第8.88轮
            ReviewFilterKind.AiTranslated => entry.Provenance == TranslationSource.AI,
            ReviewFilterKind.Modified => entry.Action == TranslationAction.TranslateModified,
            ReviewFilterKind.StoryData => TextCategoryHelper.FromRelativePath(entry.Key.RelativeFilePath) == TextCategory.StoryData,
            ReviewFilterKind.KoreanSource => ContainsHangul(entry.NewSourceText),
            ReviewFilterKind.Passthrough => entry.Provenance == TranslationSource.Passthrough,
            // 默认工作集：AI 结果且需要人工确认（直通/继承不进入）
            ReviewFilterKind.DefaultWorkSet => entry.Provenance == TranslationSource.AI
                                               && (entry.NeedsReview || entry.ValidationIssues.Count > 0),
            // 第9.0C.14轮：按问题类型
            ReviewFilterKind.JapaneseResidue => HasCode(entry, ValidationIssueCodes.JapaneseResidue),
            ReviewFilterKind.EmptyTranslation => HasCode(entry, ValidationIssueCodes.EmptyTranslation),
            ReviewFilterKind.CanonicalKoreanMissing => HasCode(entry, ValidationIssueCodes.CanonicalKoreanSourceMissing),
            _ => true,
        };
    }

    /// <summary>源文是否含韩文（与第8.5轮 SOURCE_LANGUAGE_ANOMALY 判定一致）。</summary>
    private static bool ContainsHangul(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if ((ch >= '\uAC00' && ch <= '\uD7A3') || (ch >= '\u1100' && ch <= '\u11FF') || (ch >= '\u3130' && ch <= '\u318F'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>统计 Error / Warning 数量（用于"工作量"提示）。</summary>
    public static (int Error, int Warning) CountSeverities(IEnumerable<DiffEntry> entries)
    {
        var error = 0;
        var warning = 0;
        foreach (var entry in entries)
        {
            foreach (var issue in entry.ValidationIssues)
            {
                if (issue.Severity == ValidationSeverity.Error)
                {
                    error++;
                }
                else if (issue.Severity == ValidationSeverity.Warning)
                {
                    warning++;
                }
            }
        }

        return (error, warning);
    }

    /// <summary>是否存在硬安全 Error（人工确认不能跳过结构安全校验）。</summary>
    public static bool HasHardSafetyError(DiffEntry entry)
        => entry.ValidationIssues.Any(i => i.Severity == ValidationSeverity.Error);

    private static bool HasSeverity(DiffEntry entry, ValidationSeverity severity)
        => entry.ValidationIssues.Any(i => i.Severity == severity);

    private static bool HasCode(DiffEntry entry, string code)
        => entry.ValidationIssues.Any(i => string.Equals(i.Code, code, StringComparison.Ordinal));
}
