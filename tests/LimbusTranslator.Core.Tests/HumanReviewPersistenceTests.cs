using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Review;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第1轮：人工审核写回 Translation Memory（HumanReviewed 闭环）测试。
/// </summary>
[Collection(SqliteCollection.Name)]
public class HumanReviewPersistenceTests
{
    private static TranslationMemoryOptions MakeOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_REVIEW_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TranslationMemoryOptions { DatabasePath = Path.Combine(dir, "tm.db") };
    }

    private static void Cleanup(TranslationMemoryOptions options)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path.GetDirectoryName(options.DatabasePath)!, true);
        }
        catch
        {
            // 清理失败可忽略（临时目录）
        }
    }

    private static DiffEntry MakeEntry(
        string fieldPath,
        string sourceText,
        string? translation,
        TranslationAction action = TranslationAction.TranslateNew,
        string recordId = "1")
    {
        var key = new UnitKey { RelativeFilePath = "StoryData/EN_Test.json", RecordId = recordId, FieldPath = fieldPath };
        return new DiffEntry
        {
            Key = key,
            NewSourceText = sourceText,
            DiffKind = action == TranslationAction.TranslateNew ? DiffKind.Added : DiffKind.Modified,
            Action = action,
            Translation = translation,
            NeedsReview = true,
            ReviewReason = "AI 自报歧义",
        };
    }

    [Fact]
    public void 人工审核写回_应保存HumanReviewed且清除NeedsReview()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = MakeEntry("dataList[0].content", "Reviewed sentence.", "人工修改后的译文");
            tm.Save(TranslationUnitFactory.FromDiffEntry(entry), new TranslationResult
            {
                Key = entry.Key,
                Translation = "AI 旧译文",
                Source = TranslationSource.AI,
                NeedsReview = true,
            });

            var saved = HumanReviewService.SaveReviewedEntries(new[] { entry }, tm);

            Assert.Equal(1, saved);
            Assert.False(entry.NeedsReview);
            Assert.Null(entry.ReviewReason);
            Assert.Equal(TranslationSource.HumanReviewed, entry.Provenance);

            var hit = tm.FindExactUnit(entry.Key, SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText!));
            Assert.NotNull(hit);
            Assert.Equal("人工修改后的译文", hit.Translation);
            Assert.Equal(TranslationSource.HumanReviewed, hit.Source);
            Assert.False(hit.NeedsReview);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 人工审核写回后_下一次运行优先命中HumanReviewed()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = MakeEntry("dataList[1].content", "Next run sentence.", "人工确认译文");
            Assert.Equal(1, HumanReviewService.SaveReviewedEntries(new[] { entry }, tm));

            // 模拟下一次运行：同 UnitKey + SourceHash
            var hash = SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText!);
            var hit = tm.FindExactUnit(entry.Key, hash);

            Assert.NotNull(hit);
            Assert.Equal(TranslationSource.HumanReviewed, hit.Source);
            Assert.Equal("人工确认译文", hit.Translation);
            Assert.False(hit.NeedsReview);

            // 跨 Unit 路径也不会把人工译文当作最终结果
            var otherKey = new UnitKey { RelativeFilePath = "StoryData/EN_Test.json", RecordId = "2", FieldPath = "dataList[1].content" };
            Assert.Null(tm.FindExactUnit(otherKey, hash));
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 人工审核写回_空源文不写入()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = MakeEntry("dataList[2].desc", string.Empty, "人工译文");

            var saved = HumanReviewService.SaveReviewedEntries(new[] { entry }, tm);

            Assert.Equal(0, saved);
            Assert.True(entry.NeedsReview);                     // 未写回即仍待审核
            Assert.Null(tm.FindCrossUnitSource(SqliteTranslationMemory.ComputeSourceHash(string.Empty)));
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 人工审核写回_空译文不写入()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = MakeEntry("dataList[3].flavor", "Source without translation.", "   ");

            var saved = HumanReviewService.SaveReviewedEntries(new[] { entry }, tm);

            Assert.Equal(0, saved);
            Assert.True(entry.NeedsReview);
            Assert.Null(tm.FindExactUnit(entry.Key, SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText!)));
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public void 人工审核写回_跳过已删除条目()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = MakeEntry("dataList[4].content", "Deleted sentence.", "人工译文",
                action: TranslationAction.SkipDeleted);

            var saved = HumanReviewService.SaveReviewedEntries(new[] { entry }, tm);

            Assert.Equal(0, saved);
            Assert.Null(tm.FindExactUnit(entry.Key, SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText!)));
        }
        finally
        {
            Cleanup(options);
        }
    }
}
