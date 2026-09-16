using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Diagnostics;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.5轮：真实 API 冒烟人工审阅清单必须包含"具体审核原因"。
///
/// 第8轮的问题：清单里只有 `需要人工审核: True`，人工看不出为什么。
/// </summary>
public class SmokeReviewDocumentTests
{
    private static DiffEntry Entry(bool needsReview, string? reviewReason, params ValidationIssue[] issues)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
            NewSourceText = "필립 싱클레어가 탈출장치로 후퇴",
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.TranslateMissing,
            Translation = "菲利普·辛克莱撤退到逃脱装置。",
            Provenance = TranslationSource.AI,
            NeedsReview = needsReview,
            ReviewReason = reviewReason,
            ValidationIssues = issues,
        };

    private static Dictionary<string, TranslationContext> Contexts(DiffEntry entry)
        => new(StringComparer.Ordinal) { [entry.Key.ToString()] = new TranslationContext() };

    [Fact]
    public void NeedsReview时_清单必须写出审核原因()
    {
        var entry = Entry(needsReview: true, reviewReason: "模型自报：专名不确定");

        var markdown = RealApiSmokeReviewDocument.Build(
            new[] { entry }, Contexts(entry), "测试清单", "第8.5轮");

        Assert.Contains("需要人工审核: True", markdown);
        Assert.Contains("审核原因: 模型自报：专名不确定", markdown);
        Assert.Contains("校验问题: 无", markdown);
    }

    [Fact]
    public void 无审核原因时_明确写无而不是留空()
    {
        var entry = Entry(needsReview: false, reviewReason: null);

        var markdown = RealApiSmokeReviewDocument.Build(new[] { entry }, Contexts(entry));

        Assert.Contains("审核原因: 无", markdown);
    }

    [Fact]
    public void 存在校验问题时_逐条列出Code与Message()
    {
        var entry = Entry(
            needsReview: true,
            reviewReason: "Validator: SOURCE_LANGUAGE_ANOMALY",
            new ValidationIssue
            {
                Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
                Code = ValidationIssueCodes.SourceLanguageAnomaly,
                Severity = ValidationSeverity.Warning,
                Category = ValidationCategory.Language,
                Validator = "SourceLanguageAnomalyValidator",
                Message = "源文出现韩文字符（6 个）",
            });

        var markdown = RealApiSmokeReviewDocument.Build(new[] { entry }, Contexts(entry));

        Assert.Contains("校验问题:", markdown);
        Assert.Contains("- SOURCE_LANGUAGE_ANOMALY [Warning]", markdown);
        Assert.Contains("源文出现韩文字符", markdown);
        Assert.Contains("译文来源: AI", markdown);
    }
}
