using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Review;
using LimbusTranslator.Infrastructure.Validation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.7轮：**待审核页批量替换**（纯逻辑层 <see cref="ReviewBatchReplace"/>）回归。
///
/// 真实需求：逐条看译文时发现"自己确认过的名词被翻错"（如"护士长"应为"护父"），
/// 需要一次性替换，并可同时写入术语库让下次翻译生效。替换只做字面量，且必须能被既有校验链兜住。
/// </summary>
public sealed class ReviewBatchReplaceTests
{
    private static DiffEntry Entry(string recordId, string? translation, string? source = "Source text")
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "StoryData/S949A.json", RecordId = recordId, FieldPath = "dataList[0].content" },
            NewSourceText = source,
            DiffKind = DiffKind.Modified,
            Action = TranslationAction.TranslateModified,
            Translation = translation,
        };

    // ───────── 命中统计 ─────────

    [Fact]
    public void 预览_统计命中的条目数与出现次数()
    {
        var entries = new[]
        {
            Entry("1", "良秀刺穿了粉红护士长。"),
            Entry("2", "护士长……护士长倒下了。"),
            Entry("3", "与此无关的句子。"),
        };

        var preview = ReviewBatchReplace.Preview(entries, "护士长", "护父", caseSensitive: false);

        Assert.Equal(2, preview.EntryCount);        // 命中 2 条
        Assert.Equal(3, preview.OccurrenceCount);   // 共 3 处
        Assert.False(preview.IsEmpty);
        Assert.Contains("命中 2 条 / 共 3 处", preview.Describe());
        Assert.Equal(2, preview.Samples.Count);     // 样例给前后对照
        Assert.Contains("护父", preview.Samples[0].After);
        Assert.Contains("护士长", preview.Samples[0].Before);
    }

    [Fact]
    public void 预览_区分大小写开关生效()
    {
        var entries = new[] { Entry("1", "Ring attacks. ring again.") };

        Assert.Equal(2, ReviewBatchReplace.Preview(entries, "Ring", "环", false).OccurrenceCount);
        Assert.Equal(1, ReviewBatchReplace.Preview(entries, "Ring", "环", true).OccurrenceCount);
    }

    // ───────── 执行替换 ─────────

    [Fact]
    public void 执行_只返回被改动的条目()
    {
        var hit = Entry("1", "粉红护士长");
        var other = Entry("2", "不相干的译文");
        var entries = new[] { hit, other };

        var changed = ReviewBatchReplace.Apply(entries, "护士长", "护父", caseSensitive: false);

        var only = Assert.Single(changed);
        Assert.Same(hit, only);
        Assert.Equal("粉红护父", hit.Translation);
        Assert.Equal("不相干的译文", other.Translation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 查找为空或空白_不产生任何改动(string? find)
    {
        var entry = Entry("1", "粉红护士长");

        Assert.Empty(ReviewBatchReplace.Apply(new[] { entry }, find, "护父", false));
        Assert.Equal("粉红护士长", entry.Translation);
        Assert.True(ReviewBatchReplace.Preview(new[] { entry }, find, "护父", false).IsEmpty);
    }

    [Fact]
    public void 译文为空的条目_跳过()
    {
        var entries = new[] { Entry("1", null), Entry("2", string.Empty) };

        Assert.Empty(ReviewBatchReplace.Apply(entries, "护士长", "护父", false));
        Assert.Equal(0, ReviewBatchReplace.Preview(entries, "护士长", "护父", false).EntryCount);
    }

    [Fact]
    public void 查找与替换相同时_不做无意义改动()
    {
        var entry = Entry("1", "粉红护父");

        Assert.Empty(ReviewBatchReplace.Apply(new[] { entry }, "护父", "护父", false));
    }

    // ───────── 替换后必须能被既有校验链兜住（结构安全）─────────

    [Fact]
    public void 替换保留标签时_校验通过()
    {
        var entry = Entry("1", "<i>粉红护士长</i>", "<i>Pink Nursefather</i>");

        ReviewBatchReplace.Apply(new[] { entry }, "护士长", "护父", false);
        new ValidationPipeline().ValidateAndApply(entry);

        Assert.Equal("<i>粉红护父</i>", entry.Translation);
        Assert.DoesNotContain(entry.ValidationIssues, issue => issue.Code == ValidationIssueCodes.TagMismatch);
    }

    [Fact]
    public void 替换破坏标签时_校验链必须报错()
    {
        var entry = Entry("1", "<i>粉红护士长</i>", "<i>Pink Nursefather</i>");

        // 用户误把标签一起换掉（极端情况）⇒ 结构破坏必须被硬安全校验抓到
        ReviewBatchReplace.Apply(new[] { entry }, "<i>", string.Empty, false);
        new ValidationPipeline().ValidateAndApply(entry);

        Assert.Contains(entry.ValidationIssues, issue => issue.Code == ValidationIssueCodes.TagMismatch);
    }
}
