using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Review;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第8.88轮：逐条审校筛选（Modified / StoryData / 韩文异常源 / AI / 默认工作集）。</summary>
public sealed class SequentialReviewFilterTests
{
    private static DiffEntry MakeEntry(
        string file = "Bufs.json",
        string source = "Ring attacks.",
        TranslationAction action = TranslationAction.TranslateNew,
        TranslationSource provenance = TranslationSource.AI,
        bool needsReview = false,
        IReadOnlyList<ValidationIssue>? issues = null,
        string? oldSource = null)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = "1", FieldPath = "dataList[0].name" },
            NewSourceText = source,
            OldSourceText = oldSource,
            DiffKind = DiffKind.Unchanged,
            Action = action,
            Provenance = provenance,
            NeedsReview = needsReview,
            ValidationIssues = issues ?? Array.Empty<ValidationIssue>(),
        };

    private static ValidationIssue Warn(string code) => new()
    {
        Key = new UnitKey { RelativeFilePath = "Bufs.json", RecordId = "1", FieldPath = "dataList[0].name" },
        Code = code,
        Severity = ValidationSeverity.Warning,
        Category = ValidationCategory.Language,
        Validator = "test",
        Message = "test",
    };

    private static ValidationIssue Error(string code) => new()
    {
        Key = new UnitKey { RelativeFilePath = "Bufs.json", RecordId = "1", FieldPath = "dataList[0].name" },
        Code = code,
        Severity = ValidationSeverity.Error,
        Category = ValidationCategory.Structure,
        Validator = "test",
        Message = "test",
    };

    [Fact]
    public void Modified筛选_只命中TranslateModified()
    {
        Assert.True(ReviewFilter.Matches(MakeEntry(action: TranslationAction.TranslateModified), ReviewFilterKind.Modified));
        Assert.False(ReviewFilter.Matches(MakeEntry(), ReviewFilterKind.Modified));
    }

    [Fact]
    public void Story筛选_按文件分类判定()
    {
        Assert.True(ReviewFilter.Matches(MakeEntry(file: "StoryData/3D309I.json"), ReviewFilterKind.StoryData));
        Assert.False(ReviewFilter.Matches(MakeEntry(file: "Bufs.json"), ReviewFilterKind.StoryData));
    }

    [Fact]
    public void 韩文异常源筛选_按源文判定()
    {
        Assert.True(ReviewFilter.Matches(MakeEntry(source: "사용하지 않음"), ReviewFilterKind.KoreanSource));
        Assert.False(ReviewFilter.Matches(MakeEntry(source: "Not used"), ReviewFilterKind.KoreanSource));
    }

    [Fact]
    public void AI筛选_只命中本次AI结果()
    {
        Assert.True(ReviewFilter.Matches(MakeEntry(), ReviewFilterKind.AiTranslated));
        Assert.False(ReviewFilter.Matches(MakeEntry(provenance: TranslationSource.Passthrough), ReviewFilterKind.AiTranslated));
    }

    [Fact]
    public void 直通筛选_与默认工作集互斥()
    {
        var passthrough = MakeEntry(provenance: TranslationSource.Passthrough);
        Assert.True(ReviewFilter.Matches(passthrough, ReviewFilterKind.Passthrough));
        Assert.False(ReviewFilter.Matches(passthrough, ReviewFilterKind.DefaultWorkSet));

        var plainAi = MakeEntry();
        Assert.False(ReviewFilter.Matches(plainAi, ReviewFilterKind.DefaultWorkSet));

        var aiWarning = MakeEntry(needsReview: true, issues: new[] { Warn(ValidationIssueCodes.KoreanResidue) });
        Assert.True(ReviewFilter.Matches(aiWarning, ReviewFilterKind.DefaultWorkSet));
    }

    [Fact]
    public void HardSafety判定_Error才算阻塞()
    {
        Assert.True(ReviewFilter.HasHardSafetyError(MakeEntry(issues: new[] { Error(ValidationIssueCodes.PlaceholderMismatch) })));
        Assert.False(ReviewFilter.HasHardSafetyError(MakeEntry(issues: new[] { Warn(ValidationIssueCodes.KoreanResidue) })));
    }

    [Fact]
    public void 筛选与导航顺序无关_原集合顺序不被修改()
    {
        var entries = new[] { MakeEntry(file: "A.json"), MakeEntry(file: "StoryData/B.json"), MakeEntry(file: "C.json") };
        var filtered = entries.Where(e => ReviewFilter.Matches(e, ReviewFilterKind.All)).ToList();

        Assert.Equal(entries.Select(e => e.Key.ToString()), filtered.Select(e => e.Key.ToString()));
    }
}