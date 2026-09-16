using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.5轮：SOURCE_LANGUAGE_ANOMALY（英文源文件里混入韩文）。
///
/// 规则：
///   - 检查 SourceText（源文），不是 Translation；
///   - 只作用于可翻译字段（Parser 已排除 model 等内部韩文 ID，本校验不承担字段过滤）；
///   - Warning 级：不进入 HardSafety Block，但 AI 来源下会促成 NeedsReview。
/// </summary>
public class SourceLanguageAnomalyTests
{
    private static ValidationContext Context(string source, string translation, TranslationSource? provenance)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
            SourceText = source,
            Translation = translation,
            Provenance = provenance,
        };

    [Fact]
    public void 纯英文源文_不报源语言异常()
    {
        var pipeline = new ValidationPipeline();

        var report = pipeline.Validate(Context("Philip retreats to the escape device.", "菲利普撤退到逃脱装置。", TranslationSource.AI));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.SourceLanguageAnomaly);
    }

    [Fact]
    public void 韩文整句源文_报源语言异常()
    {
        var pipeline = new ValidationPipeline();

        var report = pipeline.Validate(
            Context("필립 싱클레어가 탈출장치로 후퇴", "菲利普·辛克莱撤退到逃脱装置。", TranslationSource.AI));

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.SourceLanguageAnomaly);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(ValidationCategory.Language, issue.Category);
        Assert.Equal(nameof(SourceLanguageAnomalyValidator), issue.Validator);
        Assert.False(report.HasError);                        // 不是 HardSafety
    }

    [Fact]
    public void 英文中夹明显韩文_同样报异常()
    {
        var pipeline = new ValidationPipeline();

        var report = pipeline.Validate(
            Context("Use 필립 to unlock the door.", "使用菲利普打开门。", TranslationSource.AI));

        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.SourceLanguageAnomaly);
    }

    [Fact]
    public void AI来源下_源语言异常促成NeedsReview()
    {
        var pipeline = new ValidationPipeline();
        var entry = new DiffEntry
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
            NewSourceText = "필립 싱클레어가 탈출장치로 후퇴",
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
            Translation = "菲利普·辛克莱撤退到逃脱装置。",
            Provenance = TranslationSource.AI,
        };

        pipeline.ValidateAndApply(entry);

        Assert.True(entry.NeedsReview);
        Assert.Contains(entry.ValidationIssues, i => i.Code == ValidationIssueCodes.SourceLanguageAnomaly);
    }

    [Fact]
    public void 译文韩文残留仍由KoreanResidue负责_两个Issue可同时存在()
    {
        var pipeline = new ValidationPipeline();

        var report = pipeline.Validate(
            Context("필립 싱클레어가 탈출장치로 후퇴", "필립 싱클레어 탈출", TranslationSource.AI));

        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.SourceLanguageAnomaly);
        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.KoreanResidue);
    }

    [Fact]
    public void 空源文_不报源语言异常()
    {
        var pipeline = new ValidationPipeline();

        var report = pipeline.Validate(Context(string.Empty, string.Empty, TranslationSource.AI));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.SourceLanguageAnomaly);
    }
}
