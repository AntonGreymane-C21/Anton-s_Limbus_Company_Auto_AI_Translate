using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Snapshots;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>审核页的一个只读文本块（带语言标签与说明）。</summary>
public sealed record ReviewTextBlock(string Label, string? Text, string Note)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>供 TextBox 绑定的显示文本：缺失时给出明确文案（不显示空白）。</summary>
    public string DisplayText => HasText ? Text! : "（无）";
}

/// <summary>审核页的一条校验问题（中文名 + 说明 + 技术码）。</summary>
public sealed record ReviewIssueRow(string SeverityText, string Name, string Explanation, string Code, bool IsBlocking)
{
    /// <summary>列表里显示的一行：<c>警告｜仍有韩文残留</c>。</summary>
    public string Headline => $"{SeverityText}｜{Name}";
}

/// <summary>审核页的术语行（来源 Local / Paratranz，类型 强制 / 优先）。</summary>
public sealed record ReviewTermRow(string Source, string Target, string KindText, string SourceText)
{
    public string Headline => $"{Source} → {Target}　[{KindText}｜{SourceText}]";
}

/// <summary>
/// 审核页的**展示模型**（第9.0C轮）：把后端已有事实整理成「普通用户看得懂」的区块。
///
/// 严格边界：
///   - 只消费后端结果（<see cref="DiffEntry"/> 的模式/语言/术语/Validator 字段 + 捕获的三语文本 + 邻句上下文），
///     **不重新实现** Canonical Diff / Mode Policy / Glossary / Validator / Thinking；
///   - 语言角色（权威 / 参考 / 不使用）来自 <see cref="TranslationModePolicy"/>，UI 不自行决定；
///   - 模型自报的 needs_review 与 Validator 问题**分开呈现**。
/// </summary>
public sealed class ReviewDisplayModel
{
    public required TranslationMode Mode { get; init; }
    public required string ModeDisplayName { get; init; }
    public required string HeaderText { get; init; }
    public required string ActionText { get; init; }
    public required string DiffKindText { get; init; }
    public required string SpeakerText { get; init; }
    public required bool IsStoryData { get; init; }

    /// <summary>权威原文（KR 模式 = 韩文原文；EN_ONLY = 英文原文）。</summary>
    public required ReviewTextBlock PrimarySource { get; init; }

    /// <summary>参考原文（KR_EN = 英文；KR_JP = 日文；其余模式为 null）。</summary>
    public ReviewTextBlock? ReferenceSource { get; init; }

    /// <summary>参考区块的说明（含「该模式不使用参考译文」）。</summary>
    public required string ReferencePanelNote { get; init; }

    /// <summary>其他语言参考（折叠区；明确标注「该模式不参与翻译」）。</summary>
    public required IReadOnlyList<ReviewTextBlock> OtherLanguages { get; init; }

    public required string OldChineseText { get; init; }
    public required string CurrentTranslationText { get; init; }

    /// <summary>模型自报的待确认理由（与 Validator 问题分开）。</summary>
    public string? AiReviewReason { get; init; }

    public required IReadOnlyList<ReviewIssueRow> Issues { get; init; }
    public required string IssuePanelNote { get; init; }

    public required IReadOnlyList<ReviewTermRow> Terms { get; init; }
    public required string TermPanelNote { get; init; }

    public required string NeighborLanguageLabel { get; init; }
    public string? PreviousText { get; init; }
    public string? NextText { get; init; }
    public required string NeighborPanelNote { get; init; }

    public required string EffectiveLanguageText { get; init; }
    public required string ReviewStateText { get; init; }
    public required IReadOnlyList<string> TechnicalRows { get; init; }

    /// <summary>
    /// 构建展示模型（<paramref name="modeHint"/> 仅在条目未携带模式时兜底）。
    /// </summary>
    /// <param name="entry">后端 Diff 条目（唯一事实来源）</param>
    /// <param name="modeHint">本次运行模式（次优先，条目自带模式优先）</param>
    /// <param name="sources">本次捕获的三语文本（可空；用于「参考 / 其他语言」区块）</param>
    /// <param name="context">运行期邻句上下文（可空）</param>
    /// <param name="snapshot">本次运行的术语快照（可空；仅用于标注 Local / Paratranz 来源）</param>
    public static ReviewDisplayModel Build(
        DiffEntry entry,
        TranslationMode modeHint,
        MultilingualUnitSources? sources = null,
        TranslationContext? context = null,
        Glossary.ActiveGlossarySnapshot? snapshot = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var mode = entry.RunTranslationMode ?? modeHint;
        var option = TranslationModePresentation.Resolve(mode);
        var effective = entry.EffectiveSourceLanguage ?? ResolveEffective(mode);
        var neighborLanguage = TranslationModePolicy.GetNeighborSourceLanguage(mode);
        var isStoryData = TextCategoryHelper.FromRelativePath(entry.Key.RelativeFilePath) == TextCategory.StoryData;

        var issues = BuildIssues(entry);
        var terms = BuildTerms(entry, snapshot);

        return new ReviewDisplayModel
        {
            Mode = mode,
            ModeDisplayName = option.DisplayName,
            HeaderText = entry.Key.ToString(),
            ActionText = WorkflowText.Action(entry.Action),
            DiffKindText = WorkflowText.DiffKind(entry.DiffKind),
            SpeakerText = string.IsNullOrWhiteSpace(entry.Speaker) ? "（无）" : entry.Speaker!,
            IsStoryData = isStoryData,
            PrimarySource = BuildPrimary(entry, mode),
            ReferenceSource = BuildReference(entry, mode, sources),
            ReferencePanelNote = BuildReferenceNote(mode),
            OtherLanguages = BuildOtherLanguages(entry, mode, sources),
            OldChineseText = string.IsNullOrWhiteSpace(entry.OldTranslation) ? "（无旧中文）" : entry.OldTranslation!,
            CurrentTranslationText = entry.Translation ?? string.Empty,
            AiReviewReason = string.IsNullOrWhiteSpace(entry.ReviewReason) ? null : entry.ReviewReason,
            Issues = issues,
            IssuePanelNote = issues.Count == 0
                ? "未发现校验问题"
                : $"共 {issues.Count} 项（错误 {issues.Count(i => i.IsBlocking)} / 警告 {issues.Count(i => !i.IsBlocking)}）",
            Terms = terms,
            TermPanelNote = terms.Count == 0 ? "未命中术语" : $"已命中 {terms.Count} 项",
            NeighborLanguageLabel = $"上下文语言：{WorkflowText.Language(neighborLanguage)}",
            PreviousText = context?.Previous?.SourceText,
            NextText = context?.Next?.SourceText,
            NeighborPanelNote = BuildNeighborNote(context, isStoryData),
            EffectiveLanguageText = WorkflowText.Language(effective),
            ReviewStateText = BuildReviewState(entry),
            TechnicalRows = BuildTechnicalRows(entry, mode, effective, neighborLanguage),
        };
    }

    // ---------------- 私有构造（只读后端事实 + 文案映射） ----------------

    private static ReviewTextBlock BuildPrimary(DiffEntry entry, TranslationMode mode)
    {
        if (mode == TranslationMode.EnglishOnly)
        {
            return new ReviewTextBlock("英文原文", entry.NewSourceText, "权威来源：官方英文文本");
        }

        return new ReviewTextBlock(
            "韩文原文（权威）",
            entry.CanonicalKoreanText,
            entry.CanonicalKoreanText is null ? "缺少韩文原文：该条目需要人工检查" : "权威来源：官方韩文原文");
    }

    private static ReviewTextBlock? BuildReference(DiffEntry entry, TranslationMode mode, MultilingualUnitSources? sources)
        => mode switch
        {
            TranslationMode.KoreanEnglish => new ReviewTextBlock(
                "英文参考",
                sources?.English ?? entry.NewSourceText,
                "仅作参考；与韩文原文冲突时以韩文为准"),
            TranslationMode.KoreanJapanese => new ReviewTextBlock(
                "日文参考",
                sources?.Japanese,
                sources?.Japanese is null ? "本次没有取到日文文本" : "仅作参考；与韩文原文冲突时以韩文为准"),
            _ => null,
        };

    private static string BuildReferenceNote(TranslationMode mode) => mode switch
    {
        TranslationMode.EnglishOnly => "该模式不使用参考译文（只翻译官方英文文本）。",
        TranslationMode.KoreanEnglish => "英文仅作为辅助参考。",
        TranslationMode.KoreanJapanese => "日文仅作为辅助参考。",
        TranslationMode.KoreanOnly => "该模式不使用参考译文（仅根据韩文原文翻译）。",
        _ => string.Empty,
    };

    private static IReadOnlyList<ReviewTextBlock> BuildOtherLanguages(
        DiffEntry entry,
        TranslationMode mode,
        MultilingualUnitSources? sources)
    {
        var primary = mode == TranslationMode.EnglishOnly ? SourceLanguage.English : SourceLanguage.Korean;
        var reference = mode switch
        {
            TranslationMode.KoreanEnglish => (SourceLanguage?)SourceLanguage.English,
            TranslationMode.KoreanJapanese => SourceLanguage.Japanese,
            _ => null,
        };

        var blocks = new List<ReviewTextBlock>();
        foreach (var language in new[] { SourceLanguage.Korean, SourceLanguage.English, SourceLanguage.Japanese })
        {
            if (language == primary || language == reference)
            {
                continue;
            }

            var text = language switch
            {
                SourceLanguage.Korean => sources?.Korean ?? entry.CanonicalKoreanText,
                SourceLanguage.English => sources?.English,
                SourceLanguage.Japanese => sources?.Japanese,
                _ => null,
            };

            blocks.Add(new ReviewTextBlock(
                $"{WorkflowText.Language(language)}参考（该模式不参与翻译）",
                text,
                "仅供人工浏览，不参与本次翻译"));
        }

        return blocks;
    }

    private static IReadOnlyList<ReviewIssueRow> BuildIssues(DiffEntry entry)
        => entry.ValidationIssues
            .Select(issue =>
            {
                var (name, explanation) = WorkflowText.Issue(issue.Code);
                return new ReviewIssueRow(
                    WorkflowText.Severity(issue.Severity),
                    name,
                    explanation,
                    issue.Code,
                    WorkflowText.IsBlocking(issue.Severity));
            })
            .ToList();

    private static IReadOnlyList<ReviewTermRow> BuildTerms(
        DiffEntry entry,
        Glossary.ActiveGlossarySnapshot? snapshot)
    {
        if (entry.MatchedTerms is not { Count: > 0 })
        {
            return Array.Empty<ReviewTermRow>();
        }

        var rows = new List<ReviewTermRow>(entry.MatchedTerms.Count);
        foreach (var term in entry.MatchedTerms)
        {
            var source = snapshot is not null && snapshot.Entries.TryGetValue(term.Source, out var glossaryEntry)
                ? glossaryEntry.Source.ToString()
                : "术语表";
            rows.Add(new ReviewTermRow(
                term.Source,
                term.Target,
                term.Locked ? "强制" : "优先",
                source));
        }

        return rows;
    }

    private static string BuildNeighborNote(TranslationContext? context, bool isStoryData)
    {
        if (!isStoryData)
        {
            return "该条目不是 StoryData 对白，按现有规则不提供邻句上下文。";
        }

        return context is { HasNeighbors: true }
            ? "来自运行前快照（只读）；不参与译文编辑。"
            : "本次没有取到邻句上下文。";
    }

    private static string BuildReviewState(DiffEntry entry)
    {
        if (entry.NeedsReview)
        {
            return "需要人工确认";
        }

        return entry.Provenance == TranslationSource.HumanReviewed ? "已人工确认" : "已完成";
    }

    private static IReadOnlyList<string> BuildTechnicalRows(
        DiffEntry entry,
        TranslationMode mode,
        SourceLanguage effective,
        SourceLanguage neighborLanguage)
        => new[]
        {
            $"UnitKey：{entry.Key}",
            $"处理动作（内部值）：{entry.Action}｜变化类型（内部值）：{entry.DiffKind}",
            $"翻译模式（内部值）：{TranslationModeCodes.ToCode(mode)}",
            $"实际翻译来源（内部值）：{SourceLanguageHelper.ToCode(effective)}",
            $"邻句上下文来源（内部值）：{SourceLanguageHelper.ToCode(neighborLanguage)}",
            $"韩文原文（Canonical）存在：{(entry.CanonicalKoreanText is null ? "否" : "是")}"
            + $"｜旧韩文存在：{(entry.OldCanonicalKoreanText is null ? "否" : "是")}",
            $"TM 命中：{entry.TmMatchType}｜盐（SourceHashSalt）：{(entry.SourceHashSalt is null ? "无（EN_ONLY 兼容旧 TM）" : "有")}",
            $"英文参考是否变化：{DescribeFlag(entry.EnglishReferenceChanged)}｜日文参考是否变化：{DescribeFlag(entry.JapaneseReferenceChanged)}",
        };

    private static string DescribeFlag(bool? value) => value is null ? "不适用" : value.Value ? "是" : "否";

    private static SourceLanguage ResolveEffective(TranslationMode mode) => mode switch
    {
        TranslationMode.KoreanOnly => SourceLanguage.Korean,
        TranslationMode.KoreanJapanese => SourceLanguage.Japanese,
        _ => SourceLanguage.English,
    };
}




