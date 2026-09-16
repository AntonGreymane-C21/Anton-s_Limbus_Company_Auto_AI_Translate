using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Review;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第8.89轮：逐条审校导航器 / 通过判定 / 未保存保护（纯逻辑，无 WPF 依赖）。</summary>
public sealed class SequentialReviewNavigatorTests
{
    private static DiffEntry Entry(int i, IReadOnlyList<ValidationIssue>? issues = null)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "A.json", RecordId = i.ToString(), FieldPath = "dataList[0].name" },
            NewSourceText = "Source " + i,
            Translation = "译文 " + i,
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.TranslateNew,
            Provenance = TranslationSource.AI,
            ValidationIssues = issues ?? Array.Empty<ValidationIssue>(),
        };

    private static ValidationIssue Error(string code) => new()
    {
        Key = new UnitKey { RelativeFilePath = "A.json", RecordId = "1", FieldPath = "dataList[0].name" },
        Code = code,
        Severity = ValidationSeverity.Error,
        Category = ValidationCategory.Structure,
        Validator = "test",
        Message = "test",
    };

    [Fact]
    public void 空集合_无当前条目且两向都不可移动()
    {
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(Array.Empty<DiffEntry>());

        Assert.Null(nav.Current);
        Assert.False(nav.CanPrevious);
        Assert.False(nav.CanNext);
        Assert.Equal("第 0 / 0 条", nav.PositionText);
        Assert.False(nav.MoveNext());
        Assert.False(nav.MovePrevious());
    }

    [Fact]
    public void 单条集合_两向都不可移动且位置为1of1()
    {
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(new[] { Entry(1) });

        Assert.NotNull(nav.Current);
        Assert.False(nav.CanPrevious);
        Assert.False(nav.CanNext);
        Assert.Equal("第 1 / 1 条", nav.PositionText);
    }

    [Fact]
    public void 第一条_上一条不可用_最后一条_下一条不可用()
    {
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(new[] { Entry(1), Entry(2), Entry(3) });

        Assert.False(nav.CanPrevious);
        Assert.True(nav.CanNext);

        Assert.True(nav.MoveNext());
        Assert.True(nav.MoveNext());
        Assert.Equal("第 3 / 3 条", nav.PositionText);
        Assert.False(nav.CanNext);
        Assert.True(nav.CanPrevious);
        Assert.False(nav.MoveNext());
        Assert.Equal(2, nav.CurrentIndex);
    }

    [Fact]
    public void 上一条下一条_移动正确()
    {
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(new[] { Entry(1), Entry(2), Entry(3) });

        Assert.True(nav.MoveNext());
        Assert.Equal(1, nav.CurrentIndex);
        Assert.True(nav.MovePrevious());
        Assert.Equal(0, nav.CurrentIndex);
        Assert.False(nav.MovePrevious());
    }

    [Fact]
    public void 筛选变化_下标收敛且尽量保持当前条目()
    {
        var e1 = Entry(1);
        var e2 = Entry(2);
        var e3 = Entry(3);
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(new[] { e1, e2, e3 });
        nav.MoveNext();

        nav.ApplyFilteredEntries(new[] { e1, e2 });
        Assert.Equal(e2.Key.ToString(), nav.Current!.Key.ToString());
        Assert.False(nav.CanNext);

        nav.ApplyFilteredEntries(new[] { e1 });
        Assert.Equal(0, nav.CurrentIndex);
        Assert.Equal(e1.Key.ToString(), nav.Current!.Key.ToString());
    }

    [Fact]
    public void Reset_回到第一条()
    {
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(new[] { Entry(1), Entry(2) });
        nav.MoveNext();
        nav.Reset();
        Assert.Equal(0, nav.CurrentIndex);
    }

    [Fact]
    public void 通过并下一条_无Error时前进()
    {
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(new[] { Entry(1), Entry(2) });

        Assert.True(ReviewApprovalDecision.ShouldAdvanceAfterApprove(nav.Current));
        Assert.Null(ReviewApprovalDecision.DescribeBlock(nav.Current));
        Assert.True(nav.MoveNext());
        Assert.Equal(1, nav.CurrentIndex);
    }

    [Fact]
    public void 通过并下一条_HardSafetyError时不得前进()
    {
        var blocked = Entry(1, new[] { Error(ValidationIssueCodes.PlaceholderMismatch) });
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(new[] { blocked, Entry(2) });

        Assert.False(ReviewApprovalDecision.ShouldAdvanceAfterApprove(nav.Current));
        Assert.Contains(ValidationIssueCodes.PlaceholderMismatch, ReviewApprovalDecision.DescribeBlock(nav.Current)!);
        Assert.Equal(0, nav.CurrentIndex);
    }

    [Fact]
    public void 通过最后一条_不越界()
    {
        var nav = new SequentialReviewNavigator();
        nav.ApplyFilteredEntries(new[] { Entry(1), Entry(2) });
        nav.MoveNext();

        Assert.True(ReviewApprovalDecision.ShouldAdvanceAfterApprove(nav.Current));
        Assert.False(nav.MoveNext());
        Assert.Equal(1, nav.CurrentIndex);
    }
}