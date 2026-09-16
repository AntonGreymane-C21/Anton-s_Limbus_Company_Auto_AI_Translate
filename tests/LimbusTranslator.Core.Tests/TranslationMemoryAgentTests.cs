using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Translation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第1轮：TranslationAgent 与 Translation Memory 的交互测试。
/// 覆盖：ExactUnit 命中、NeedsReview / 来源传播、Provider 调用次数、空源文不落库。
/// </summary>
[Collection(SqliteCollection.Name)]
public class TranslationMemoryAgentTests
{
    /// <summary>统计调用次数的测试用 Provider（不访问网络）。</summary>
    private sealed class CountingProvider : ITranslationProvider
    {
        public int CallCount { get; private set; }

        public int LastBatchCount { get; private set; }

        public string Translation { get; init; } = "AI 译文";

        public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
            IReadOnlyList<DiffEntry> entries,
            CancellationToken cancellationToken = default,
            string? stageId = null,
            IReadOnlyDictionary<string, TranslationContext>? contexts = null)
        {
            CallCount++;
            LastBatchCount = entries.Count;

            var results = new Dictionary<string, TranslationResult>();
            foreach (var entry in entries)
            {
                results[entry.Key.ToString()] = new TranslationResult
                {
                    Key = entry.Key,
                    Translation = Translation,
                    Source = TranslationSource.AI,
                };
            }

            return Task.FromResult<IReadOnlyDictionary<string, TranslationResult>>(results);
        }
    }

    private static TranslationMemoryOptions MakeOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_TM_AGENT_" + Guid.NewGuid().ToString("N"));
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

    private static DiffEntry MakeEntry(string fieldPath, string sourceText, string recordId = "1")
    {
        var key = new UnitKey { RelativeFilePath = "StoryData/EN_Test.json", RecordId = recordId, FieldPath = fieldPath };
        return new DiffEntry
        {
            Key = key,
            NewSourceText = sourceText,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };
    }

    private static TranslationUnit MakeUnit(DiffEntry entry) => TranslationUnitFactory.FromDiffEntry(entry);

    private static Task<AgentExecutionResult> RunAgentAsync(
        CountingProvider provider,
        SqliteTranslationMemory memory,
        params DiffEntry[] entries)
    {
        var agent = new TranslationAgent(provider, new RateLimitManager(4, 4), memory);
        return agent.ExecuteAsync("StoryData/EN_Test.json", entries);
    }
    [Fact]
    public async Task TM命中_NeedsReview与来源应传播且不调用Provider()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = MakeEntry("dataList[0].content", "Cached sentence.");
            tm.Save(MakeUnit(entry), new TranslationResult
            {
                Key = entry.Key,
                Translation = "缓存译文",
                Source = TranslationSource.AI,
                NeedsReview = true,
                ReviewReason = "上轮标记歧义",
            });

            var provider = new CountingProvider();
            var result = await RunAgentAsync(provider, tm, entry);

            Assert.True(result.IsSuccess);
            Assert.Equal(0, provider.CallCount);                 // TM 命中：不调用 Provider
            Assert.Equal(1, result.NeedsReviewCount);            // NeedsReview 不能丢
            Assert.Equal("缓存译文", entry.Translation);
            Assert.True(entry.NeedsReview);
            Assert.Equal("上轮标记歧义", entry.ReviewReason);
            Assert.Equal(TranslationSource.AI, entry.Provenance);
            Assert.Equal(TranslationMemoryMatchType.ExactUnit, entry.TmMatchType);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task HumanReviewed命中_不调用Provider()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = MakeEntry("dataList[1].content", "Human reviewed sentence.");
            Assert.True(tm.SaveHumanReviewed(MakeUnit(entry), "人工确认译文"));

            var provider = new CountingProvider();
            var result = await RunAgentAsync(provider, tm, entry);

            Assert.True(result.IsSuccess);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal("人工确认译文", entry.Translation);
            Assert.Equal(TranslationSource.HumanReviewed, entry.Provenance);
            Assert.False(entry.NeedsReview);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task 相同SourceHash不同UnitKey_仍调用Provider()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var saved = MakeEntry("dataList[0].content", "Shared text across units.", recordId: "1");
            tm.Save(MakeUnit(saved), new TranslationResult
            {
                Key = saved.Key,
                Translation = "旧位置译文",
                Source = TranslationSource.AI,
            });

            // 不同 UnitKey，但英文完全相同
            var other = MakeEntry("dataList[7].content", "Shared text across units.", recordId: "7");
            var provider = new CountingProvider { Translation = "新位置译文" };
            var result = await RunAgentAsync(provider, tm, other);

            Assert.True(result.IsSuccess);
            Assert.Equal(1, provider.CallCount);                 // 禁止跨 Unit 静默复用
            Assert.Equal(1, provider.LastBatchCount);
            Assert.Equal("新位置译文", other.Translation);
            Assert.Equal(TranslationMemoryMatchType.None, other.TmMatchType);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task 空源文条目_不写入TranslationMemory()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var blank = MakeEntry("dataList[3].desc", string.Empty);
            var provider = new CountingProvider();

            var result = await RunAgentAsync(provider, tm, blank);

            Assert.True(result.IsSuccess);
            // 空源文（以及空字符串哈希）绝不允许沉淀成可被复用的 TM 记录
            var emptyHash = SqliteTranslationMemory.ComputeSourceHash(string.Empty);
            Assert.Null(tm.FindCrossUnitSource(emptyHash));
            Assert.Null(tm.FindExactUnit(blank.Key, emptyHash));
        }
        finally
        {
            Cleanup(options);
        }
    }

}
