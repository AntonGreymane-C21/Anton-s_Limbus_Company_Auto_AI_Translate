using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Review;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.14轮：待审核页**按问题类型一键筛选**（用户反馈：想集中修某一类问题，
/// 例如"把继承来的 し协会 统一改成社区译名"）。
/// </summary>
public sealed class ReviewFilterIssueTypeTests
{
    private static DiffEntry Entry(params string[] codes)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "StoryData/X.json", RecordId = "1", FieldPath = "dataList[0].content" },
            NewSourceText = "Hello",
            Translation = "你好",
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.TranslateModified,
            ValidationIssues = codes
                .Select(code => new ValidationIssue
                {
                    Key = new UnitKey { RelativeFilePath = "StoryData/X.json", RecordId = "1", FieldPath = "dataList[0].content" },
                    Code = code,
                    Severity = ValidationSeverity.Warning,
                    Category = ValidationCategory.Language,
                    Validator = "test",
                    Message = code,
                })
                .ToList(),
        };

    [Fact]
    public void 日文假名残留可单独筛选()
        => Assert.True(ReviewFilter.Matches(Entry(ValidationIssueCodes.JapaneseResidue), ReviewFilterKind.JapaneseResidue));

    [Fact]
    public void 空译文可单独筛选()
        => Assert.True(ReviewFilter.Matches(Entry(ValidationIssueCodes.EmptyTranslation), ReviewFilterKind.EmptyTranslation));

    [Fact]
    public void 缺韩文原文可单独筛选()
        => Assert.True(ReviewFilter.Matches(Entry(ValidationIssueCodes.CanonicalKoreanSourceMissing), ReviewFilterKind.CanonicalKoreanMissing));

    [Fact]
    public void 韩文残留筛选不会命中日文假名条目()
        => Assert.False(ReviewFilter.Matches(Entry(ValidationIssueCodes.JapaneseResidue), ReviewFilterKind.KoreanResidue));

    [Theory]
    [InlineData("只看 JAPANESE_RESIDUE（日文假名残留）")]
    [InlineData("只看 EMPTY_TRANSLATION（空译文）")]
    [InlineData("只看 CANONICAL_KOREAN_SOURCE_MISSING（缺韩文原文）")]
    public void 下拉文案能解析为对应筛选类型(string displayName)
    {
        var kind = ReviewFilter.Parse(displayName);

        Assert.NotEqual(ReviewFilterKind.All, kind);
        Assert.Equal(displayName, ReviewFilter.DisplayNames[(int)kind]);
    }
}
