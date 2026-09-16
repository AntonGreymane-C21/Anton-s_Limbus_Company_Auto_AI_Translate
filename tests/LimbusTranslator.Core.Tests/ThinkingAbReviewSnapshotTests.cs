using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Diagnostics;

namespace LimbusTranslator.Core.Tests;

public class ThinkingAbReviewSnapshotTests
{
    [Fact]
    public void Capture_应冻结本组译文与审核状态_不受下一组覆盖()
    {
        var entry = new DiffEntry
        {
            Key = new UnitKey { RelativeFilePath = "BattleSpeechBubbleDlg.json", RecordId = "1", FieldPath = "dataList[0].desc" },
            NewSourceText = "필립 싱클레어가 탈출장치로 후퇴",
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateMissing,
            Translation = "菲利普辛克莱通过逃生装置撤退",
            Provenance = TranslationSource.AI,
            NeedsReview = true,
            ReviewReason = "SOURCE_LANGUAGE_ANOMALY: 源文出现韩文字符",
        };
        entry.ValidationIssues = new[] { Issue(entry, ValidationIssueCodes.SourceLanguageAnomaly) };

        var thinkingOn = ThinkingAbReviewSnapshot.Capture(entry, Result(entry, entry.Translation!));

        // 模拟 B 组运行复用同一业务 Entry 后覆盖运行期状态。
        entry.Translation = entry.NewSourceText;
        entry.NeedsReview = true;
        entry.ReviewReason = "KOREAN_RESIDUE: 译文中检测到韩文字符残留";
        entry.ValidationIssues = new[]
        {
            Issue(entry, ValidationIssueCodes.SourceLanguageAnomaly),
            Issue(entry, ValidationIssueCodes.KoreanResidue),
            Issue(entry, ValidationIssueCodes.TerminologyMismatch),
        };
        var thinkingOff = ThinkingAbReviewSnapshot.Capture(entry, Result(entry, entry.Translation!));

        Assert.Equal("菲利普辛克莱通过逃生装置撤退", thinkingOn.Translation);
        Assert.DoesNotContain(thinkingOn.ValidationIssues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
        Assert.DoesNotContain(thinkingOn.ValidationIssues, issue => issue.Code == ValidationIssueCodes.TerminologyMismatch);

        Assert.Equal("필립 싱클레어가 탈출장치로 후퇴", thinkingOff.Translation);
        Assert.Contains(thinkingOff.ValidationIssues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
        Assert.Contains(thinkingOff.ValidationIssues, issue => issue.Code == ValidationIssueCodes.TerminologyMismatch);
    }

    private static TranslationResult Result(DiffEntry entry, string translation) => new()
    {
        Key = entry.Key,
        Translation = translation,
        Source = TranslationSource.AI,
    };

    private static ValidationIssue Issue(DiffEntry entry, string code) => new()
    {
        Key = entry.Key,
        Code = code,
        Severity = ValidationSeverity.Warning,
        Category = ValidationCategory.Language,
        Validator = "Fixture",
        Message = code,
    };
}
