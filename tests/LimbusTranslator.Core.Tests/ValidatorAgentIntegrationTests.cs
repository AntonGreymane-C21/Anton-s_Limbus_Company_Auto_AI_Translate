using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第2轮：TranslationAgent 与 ValidatorPipeline 的集成测试。
/// 验证 TM 命中（第1轮建立的 ExactUnit）也会重新执行当前校验规则。
/// </summary>
[Collection(SqliteCollection.Name)]
public class ValidatorAgentIntegrationTests
{
    private sealed class CountingProvider : ITranslationProvider
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
            IReadOnlyList<DiffEntry> entries,
            CancellationToken cancellationToken = default,
            string? stageId = null,
            IReadOnlyDictionary<string, TranslationContext>? contexts = null)
        {
            CallCount++;
            var results = new Dictionary<string, TranslationResult>();
            foreach (var entry in entries)
            {
                results[entry.Key.ToString()] = new TranslationResult
                {
                    Key = entry.Key,
                    Translation = "新译文。",
                    Source = TranslationSource.AI,
                };
            }
            return Task.FromResult<IReadOnlyDictionary<string, TranslationResult>>(results);
        }
    }

    private static TranslationMemoryOptions MakeOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_VALAGENT_" + Guid.NewGuid().ToString("N"));
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

    private static DiffEntry Entry(string source)
    {
        var key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" };
        return new DiffEntry
        {
            Key = key,
            NewSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };
    }

    [Fact]
    public async Task TM命中后_会重新执行Validator并发现旧缓存问题()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = Entry("Cached English source.");
            var unit = TranslationUnitFactory.FromDiffEntry(entry);

            // 旧缓存：源文非空但译文为空（历史脏数据），且当时未标记待审核
            tm.Save(unit, new TranslationResult
            {
                Key = entry.Key,
                Translation = string.Empty,
                Source = TranslationSource.AI,
                NeedsReview = false,
            });

            var provider = new CountingProvider();
            var agent = new TranslationAgent(
                provider,
                new RateLimitManager(4, 4),
                tm,
                validation: new ValidationPipeline());

            var result = await agent.ExecuteAsync("Test.json", new[] { entry });

            Assert.True(result.IsSuccess);
            Assert.Equal(0, provider.CallCount);                      // TM 命中：不调用 Provider
            Assert.True(entry.NeedsReview);                           // 但重新校验发现了 EMPTY_TRANSLATION
            Assert.Contains(entry.ValidationIssues, i => i.Code == ValidationIssueCodes.EmptyTranslation);
            Assert.Equal(1, result.NeedsReviewCount);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task TM命中人工确认译文_启发式Warning不改变人工确认状态()
    {
        var options = MakeOptions();
        try
        {
            using var tm = new SqliteTranslationMemory(options);
            var entry = Entry("Enemy takes damage.");
            var unit = TranslationUnitFactory.FromDiffEntry(entry);

            Assert.True(tm.SaveHumanReviewed(unit, "对敌人造成 Deal damage to the enemy 效果。"));

            var provider = new CountingProvider();
            var agent = new TranslationAgent(
                provider,
                new RateLimitManager(4, 4),
                tm,
                validation: new ValidationPipeline());

            var result = await agent.ExecuteAsync("Test.json", new[] { entry });

            Assert.True(result.IsSuccess);
            Assert.Equal(0, provider.CallCount);
            Assert.Equal(TranslationSource.HumanReviewed, entry.Provenance);
            Assert.False(entry.NeedsReview);                          // 人工确认语义不被普通 Warning 抹掉
            Assert.Contains(entry.ValidationIssues, i => i.Code == ValidationIssueCodes.EnglishResidue);
            Assert.Equal(0, result.NeedsReviewCount);
        }
        finally
        {
            Cleanup(options);
        }
    }
}
