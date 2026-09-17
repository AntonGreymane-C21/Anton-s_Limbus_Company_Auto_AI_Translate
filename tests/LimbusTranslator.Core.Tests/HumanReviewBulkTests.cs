using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Review;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.24轮：**人工审核批量写回（单事务）** 回归。
///
/// 真实故障：`PersistHumanReviewedEntries` 对**本轮全部译文**（可能 10 万条）逐条
/// <c>SaveHumanReviewed</c> ⇒ 每条一个新连接 + 一个新事务，且跑在 UI 线程 ⇒ 点「审核后重新输出」后界面长时间假死。
/// 现在改为：只回写人工确认过的条目 + 后台执行 + `SaveMany` 单事务。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class HumanReviewBulkTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "LT_HRBulk_" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            // 清理失败可忽略
        }
    }

    private SqliteTranslationMemory Open()
        => new(new TranslationMemoryOptions { DatabasePath = _dbPath });

    private static DiffEntry Entry(
        string file,
        string source,
        string? translation,
        TranslationAction action = TranslationAction.TranslateModified)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = "1", FieldPath = "dataList[0].content" },
            NewSourceText = source,
            DiffKind = DiffKind.Modified,
            Action = action,
            Translation = translation,
        };

    [Fact]
    public void 批量写回_写入全部有效条目并标记人工确认()
    {
        var entries = new[]
        {
            Entry("StoryData/A.json", "안녕", "你好"),
            Entry("StoryData/B.json", "감사", "谢谢"),
        };

        using var memory = Open();
        var saved = HumanReviewService.SaveReviewedEntriesBulk(entries, memory);

        Assert.Equal(2, saved);
        Assert.All(entries, entry =>
        {
            Assert.Equal(TranslationSource.HumanReviewed, entry.Provenance);
            Assert.False(entry.NeedsReview);
            Assert.Null(entry.ReviewReason);
        });

        // 落库内容与逐条版一致（可被精确命中读回）
        foreach (var entry in entries)
        {
            var hash = SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText!, entry.SourceHashSalt);
            var hit = memory.FindExactUnit(entry.Key, hash);
            Assert.NotNull(hit);
            Assert.Equal(entry.Translation, hit!.Translation);
            Assert.Equal(TranslationSource.HumanReviewed, hit.Source);
            Assert.False(hit.NeedsReview);
        }
    }

    [Fact]
    public void 批量写回_跳过删除项空源文与空译文()
    {
        var entries = new[]
        {
            Entry("StoryData/Skip.json", "텍스트", "译文", TranslationAction.SkipDeleted),
            Entry("StoryData/EmptySource.json", "   ", "译文"),
            Entry("StoryData/EmptyTranslation.json", "텍스트", null),
            Entry("StoryData/Ok.json", "텍스트", "正常译文"),
        };

        using var memory = Open();
        var saved = HumanReviewService.SaveReviewedEntriesBulk(entries, memory);

        Assert.Equal(1, saved);
        Assert.Equal(TranslationSource.HumanReviewed, entries[3].Provenance);
        Assert.Null(entries[2].Provenance);            // 空译文未被标记
    }

    [Fact]
    public void 批量写回_空集合返回零()
    {
        using var memory = Open();

        Assert.Equal(0, HumanReviewService.SaveReviewedEntriesBulk(Array.Empty<DiffEntry>(), memory));
    }

    [Fact]
    public void 批量写回_与逐条写回语义一致()
    {
        var bulkEntry = Entry("StoryData/Bulk.json", "텍스트", "批量译文");
        var singleEntry = Entry("StoryData/Single.json", "텍스트", "逐条译文");

        using var memory = Open();
        HumanReviewService.SaveReviewedEntriesBulk(new[] { bulkEntry }, memory);
        HumanReviewService.SaveReviewedEntries(new[] { singleEntry }, memory);

        var bulkHash = SqliteTranslationMemory.ComputeSourceHash(bulkEntry.NewSourceText!, null);
        var singleHash = SqliteTranslationMemory.ComputeSourceHash(singleEntry.NewSourceText!, null);

        var bulk = memory.FindExactUnit(bulkEntry.Key, bulkHash);
        var single = memory.FindExactUnit(singleEntry.Key, singleHash);

        Assert.Equal(single!.Source, bulk!.Source);
        Assert.Equal(single.NeedsReview, bulk.NeedsReview);
        Assert.Equal(single.ReviewReason, bulk.ReviewReason);
    }
}
