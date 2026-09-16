using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第4轮：request_cache 行为测试。
/// 全部使用临时 SQLite + Fake Provider（不访问真实 API、不写真实数据库）。
/// </summary>
[Collection(SqliteCollection.Name)]
public class RequestCacheTests
{
    /// <summary>可注入的 Fake 批量客户端（不访问网络）。</summary>
    private sealed class FakeBatchClient : IDeepSeekBatchClient
    {
        public int CallCount { get; private set; }

        /// <summary>返回译文的方式（默认回显 Placeholder 保护文本，保持占位符完整）</summary>
        public Func<DeepSeekTranslateRequestItem, string> Translate { get; init; } = item => $"[译]{item.Source}";

        public Exception? ThrowOnCall { get; init; }

        public int RetryCount { get; init; }

        public string? ResponseId { get; init; } = "resp-1";

        public string? ResponseModel { get; init; } = "deepseek-v4-flash";

        public int? PromptTokens { get; init; } = 10;

        public int? CompletionTokens { get; init; } = 20;

        public int? TotalTokens { get; init; } = 30;

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
        {
            CallCount++;
            if (ThrowOnCall is not null)
            {
                throw ThrowOnCall;
            }

            var results = items.ToDictionary(
                i => i.Id,
                i => new DeepSeekTranslateItem(i.Id, Translate(i), false, string.Empty),
                StringComparer.Ordinal);

            return Task.FromResult(new DeepSeekBatchResult
            {
                Items = results,
                ResponseId = ResponseId,
                ResponseModel = ResponseModel,
                PromptTokens = PromptTokens,
                CompletionTokens = CompletionTokens,
                TotalTokens = TotalTokens,
                RetryCount = RetryCount,
                DurationMs = 5,
            });
        }
    }

    private static TranslationMemoryOptions MakeOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_REQCACHE_" + Guid.NewGuid().ToString("N"));
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

    private static int CountCacheRows(TranslationMemoryOptions options)
    {
        using var conn = new SqliteConnection($"Data Source={options.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM request_cache;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static DeepSeekTranslationProvider MakeProvider(
        TranslationMemoryOptions options,
        IDeepSeekBatchClient client,
        IRequestCache? cache = null,
        TranslationCacheServices? services = null,
        string model = "deepseek-v4-flash")
        => new(
            new DeepSeekOptions
            {
                ApiKey = "test-key",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = model,
                MaxRetry = 0,
            },
            configDir: null,
            cacheServices: services
                ?? (cache is null
                    ? null
                    : TranslationCacheServices.Create(cache, TranslationRunContext.Create())),
            client: client);

    private static DiffEntry MakeEntry(string fieldPath = "dataList[0].content", string source = "Deal 20 damage.", string recordId = "1")
    {
        var key = new UnitKey { RelativeFilePath = "Test.json", RecordId = recordId, FieldPath = fieldPath };
        return new DiffEntry
        {
            Key = key,
            NewSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };
    }

    [Fact]
    public async Task 第一次请求_Miss并写入缓存()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());
            var client = new FakeBatchClient();
            var provider = MakeProvider(options, client, services: services);

            var entry = MakeEntry();
            var results = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");
            services.Staging!.FlushAll();

            var result = results[entry.Key.ToString()];
            Assert.False(result.CacheHit);
            Assert.Equal(1, client.CallCount);
            Assert.NotNull(result.RequestFingerprint);
            Assert.StartsWith(RequestFingerprintBuilder.FingerprintPrefix + ":", result.RequestFingerprint);
            Assert.Equal(1, CountCacheRows(options));
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task 第二次相同请求_Hit且不调用Provider()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());
            var client = new FakeBatchClient();
            var provider = MakeProvider(options, client, services: services);
            var entry = MakeEntry();

            var first = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");
            services.Staging!.FlushAll();
            var second = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");

            Assert.Equal(1, client.CallCount);                       // 命中缓存：不再调用 Provider
            Assert.False(first[entry.Key.ToString()].CacheHit);
            Assert.True(second[entry.Key.ToString()].CacheHit);
            Assert.Equal(first[entry.Key.ToString()].Translation, second[entry.Key.ToString()].Translation);
            Assert.Equal(1, CountCacheRows(options));                // 同指纹不会无限增长
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task CacheHit后仍重新执行当前Validator()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());

            // 缓存内容故意丢掉数字 20 → Cache Hit 后当前 Validator 必须报 NUMBER_MISMATCH
            var client = new FakeBatchClient { Translate = _ => "对敌人造成伤害。" };
            var provider = MakeProvider(options, client, services: services);
            var entry = MakeEntry(source: "Deal 20 damage.");

            await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");
            services.Staging!.FlushAll();

            var secondClient = new FakeBatchClient();
            var secondProvider = MakeProvider(options, secondClient, services: services);
            var secondEntry = MakeEntry(source: "Deal 20 damage.");

            var agent = new TranslationAgent(
                secondProvider,
                new RateLimitManager(2, 2),
                memory,
                validation: new ValidationPipeline(),
                cacheServices: services);

            var execution = await agent.ExecuteAsync("Test.json", new[] { secondEntry });

            Assert.True(execution.IsSuccess);
            Assert.Equal(0, secondClient.CallCount);                 // 来自缓存
            Assert.Contains(secondEntry.ValidationIssues, i => i.Code == ValidationIssueCodes.NumberMismatch);
            Assert.True(secondEntry.NeedsReview);                    // 当前规则重新生效
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task 硬安全Error_不写入可命中缓存()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());

            // Provider 返回空译文：Placeholder 层不报错，但当前 Validator 会判定 EMPTY_TRANSLATION
            var client = new FakeBatchClient { Translate = _ => string.Empty };
            var provider = MakeProvider(options, client, services: services);
            var entry = MakeEntry(source: "Deal 20 damage.");

            var agent = new TranslationAgent(
                provider,
                new RateLimitManager(2, 2),
                memory,
                validation: new ValidationPipeline(),
                cacheServices: services);

            await agent.ExecuteAsync("Test.json", new[] { entry });

            Assert.Contains(entry.ValidationIssues, i => i.Code == ValidationIssueCodes.EmptyTranslation);
            Assert.Equal(0, CountCacheRows(options));                // 坏响应不进入可命中缓存
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task Placeholder严重失败_不写入缓存()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());

            // 源文有 {0}，Fake 故意丢掉 → Placeholder 校验失败 → 不缓存
            var client = new FakeBatchClient { Translate = _ => "造成伤害。" };
            var provider = MakeProvider(options, client, services: services);
            var entry = MakeEntry(source: "Deal {0} damage.");

            var results = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");
            services.Staging!.FlushAll();

            Assert.True(results[entry.Key.ToString()].NeedsReview);
            Assert.Equal(0, CountCacheRows(options));
        }
        finally
        {
            Cleanup(options);
        }
    }


    [Fact]
    public async Task 损坏缓存_降级为Miss并继续翻译()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);

            // 先跑一次（关闭缓存）拿到指纹
            var warmClient = new FakeBatchClient();
            var warmProvider = MakeProvider(options, warmClient, cache: null);
            var warmEntry = MakeEntry();
            var warm = await warmProvider.TranslateAsync(new[] { warmEntry }, CancellationToken.None, "Test.json");
            var fingerprint = warm[warmEntry.Key.ToString()].RequestFingerprint!;

            // 写入损坏的 ResponseJson
            memory.SaveRequestCache(fingerprint, "{ this is not json");

            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());
            var client = new FakeBatchClient();
            var provider = MakeProvider(options, client, services: services);
            var entry = MakeEntry();

            var results = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");

            Assert.Equal(1, client.CallCount);                        // 坏缓存 → 正常调用 Provider
            Assert.False(results[entry.Key.ToString()].CacheHit);
            Assert.False(string.IsNullOrWhiteSpace(results[entry.Key.ToString()].Translation));
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task 缓存ID集合不一致_视为无效缓存()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);

            var warmClient = new FakeBatchClient();
            var warmProvider = MakeProvider(options, warmClient, cache: null);
            var warmEntry = MakeEntry();
            var warm = await warmProvider.TranslateAsync(new[] { warmEntry }, CancellationToken.None, "Test.json");
            var fingerprint = warm[warmEntry.Key.ToString()].RequestFingerprint!;

            // 写入一个 ID 集合不匹配的缓存（缺失 + 多余）
            memory.Save(fingerprint, new CachedProviderBatchResponse
            {
                Provider = "deepseek",
                Model = "deepseek-v4-flash",
                CreatedAtUtc = DateTime.UtcNow,
                Items = new[]
                {
                    new CachedProviderItem { Id = "别的.json|9|dataList[9].content", Translation = "缓存译文" },
                },
            });

            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());
            var client = new FakeBatchClient();
            var provider = MakeProvider(options, client, services: services);
            var entry = MakeEntry();

            var results = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");

            Assert.Equal(1, client.CallCount);
            Assert.False(results[entry.Key.ToString()].CacheHit);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task 不同模型_缓存不互相命中()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);

            var servicesA = TranslationCacheServices.Create(memory, TranslationRunContext.Create());
            var clientA = new FakeBatchClient();
            var providerA = MakeProvider(options, clientA, services: servicesA, model: "deepseek-v4-flash");
            var entry = MakeEntry();
            await providerA.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");
            servicesA.Staging!.FlushAll();
            Assert.Equal(1, CountCacheRows(options));

            // 换模型 → 指纹不同 → 必须 Miss
            var servicesB = TranslationCacheServices.Create(memory, TranslationRunContext.Create());
            var clientB = new FakeBatchClient();
            var providerB = MakeProvider(options, clientB, services: servicesB, model: "deepseek-v4");
            var entryB = MakeEntry();

            var results = await providerB.TranslateAsync(new[] { entryB }, CancellationToken.None, "Test.json");

            Assert.Equal(1, clientB.CallCount);
            Assert.False(results[entryB.Key.ToString()].CacheHit);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task 并发写入同指纹_不失败且只保留一行()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            const string fingerprint = "v1:concurrent-test";

            var tasks = Enumerable.Range(0, 8)
                .Select(i => Task.Run(() => memory.Save(fingerprint, new CachedProviderBatchResponse
                {
                    Provider = "deepseek",
                    Model = "deepseek-v4-flash",
                    CreatedAtUtc = DateTime.UtcNow,
                    Items = new[]
                    {
                        new CachedProviderItem { Id = "Test.json|1|a", Translation = $"译文{i}" },
                    },
                })))
                .ToArray();

            await Task.WhenAll(tasks);   // 并发写入不得抛异常

            Assert.Equal(1, CountCacheRows(options));   // 同一指纹只保留一条
        }
        finally
        {
            Cleanup(options);
        }
    }
}

