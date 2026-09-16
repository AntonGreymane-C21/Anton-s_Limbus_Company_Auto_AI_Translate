using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第4轮：Translation Trace 测试（JSONL，脱敏，线程安全）。
/// 全部使用临时目录 + Fake Provider，不访问真实 API。
/// </summary>
[Collection(SqliteCollection.Name)]
public class TranslationTraceTests
{
    private sealed class FakeBatchClient : IDeepSeekBatchClient
    {
        public int CallCount { get; private set; }

        public Func<DeepSeekTranslateRequestItem, string> Translate { get; init; } = item => $"[译]{item.Source}";

        public Exception? ThrowOnCall { get; init; }

        public int RetryCount { get; init; }

        public string? ResponseId { get; init; } = "resp-1";

        public string? ResponseModel { get; init; } = "deepseek-v4-flash";

        public int? PromptTokens { get; init; } = 11;

        public int? CompletionTokens { get; init; } = 22;

        public int? TotalTokens { get; init; } = 33;

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
                DurationMs = 7,
            });
        }
    }

    private static string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(params string[] dirs)
    {
        SqliteConnection.ClearAllPools();
        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // 清理失败可忽略
            }
        }
    }

    private static TranslationMemoryOptions MakeOptions(string root)
        => new() { DatabasePath = Path.Combine(root, "tm.db") };

    private static DeepSeekTranslationProvider MakeProvider(
        TranslationCacheServices services,
        IDeepSeekBatchClient client)
        => new(
            new DeepSeekOptions
            {
                ApiKey = "test-key",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "deepseek-v4-flash",
                MaxRetry = 0,
            },
            configDir: null,
            cacheServices: services,
            client: client);

    private static DiffEntry MakeEntry(string source = "Deal 20 damage.")
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

    private static IReadOnlyList<JsonElement> ReadTrace(TranslationTraceWriter writer)
    {
        var path = writer.FilePath!;
        if (!File.Exists(path))
        {
            return Array.Empty<JsonElement>();
        }

        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    [Fact]
    public async Task API成功_Trace记录成功与元数据()
    {
        var root = MakeTempDir("LT_TRACE_");
        try
        {
            using var memory = new SqliteTranslationMemory(MakeOptions(root));
            var run = TranslationRunContext.Create();
            var writer = new TranslationTraceWriter(root, run);
            var services = TranslationCacheServices.Create(memory, run, writer);
            var provider = MakeProvider(services, new FakeBatchClient());

            await provider.TranslateAsync(new[] { MakeEntry() }, CancellationToken.None, "Test.json");

            var entry = Assert.Single(ReadTrace(writer));
            Assert.True(entry.GetProperty("success").GetBoolean());
            Assert.False(entry.GetProperty("cacheHit").GetBoolean());
            Assert.True(entry.GetProperty("networkCalled").GetBoolean());
            Assert.Equal(run.RunId, entry.GetProperty("runId").GetString());
            Assert.Equal("Test.json", entry.GetProperty("stageId").GetString());
            Assert.Equal("deepseek", entry.GetProperty("provider").GetString());
            Assert.Equal(11, entry.GetProperty("inputTokens").GetInt32());
            Assert.Equal(22, entry.GetProperty("outputTokens").GetInt32());
            Assert.Equal(33, entry.GetProperty("totalTokens").GetInt32());
            Assert.Equal("resp-1", entry.GetProperty("responseId").GetString());
            Assert.StartsWith(RequestFingerprintBuilder.FingerprintPrefix + ":", entry.GetProperty("fingerprint").GetString());
            Assert.NotEmpty(entry.GetProperty("promptHash").GetString()!);
            Assert.NotEmpty(entry.GetProperty("contextHash").GetString()!);
            Assert.Equal("Test.json|1|dataList[0].content", entry.GetProperty("unitKeys")[0].GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task CacheHit_Trace标记命中且未调用网络()
    {
        var root = MakeTempDir("LT_TRACE_");
        try
        {
            using var memory = new SqliteTranslationMemory(MakeOptions(root));
            var run = TranslationRunContext.Create();
            var writer = new TranslationTraceWriter(root, run);
            var services = TranslationCacheServices.Create(memory, run, writer);
            var client = new FakeBatchClient();
            var provider = MakeProvider(services, client);

            await provider.TranslateAsync(new[] { MakeEntry() }, CancellationToken.None, "Test.json");
            services.Staging!.FlushAll();
            await provider.TranslateAsync(new[] { MakeEntry() }, CancellationToken.None, "Test.json");

            var lines = ReadTrace(writer);
            Assert.Equal(2, lines.Count);
            Assert.False(lines[0].GetProperty("cacheHit").GetBoolean());
            Assert.True(lines[1].GetProperty("cacheHit").GetBoolean());
            Assert.False(lines[1].GetProperty("networkCalled").GetBoolean());
            Assert.Equal(1, client.CallCount);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task API失败_Trace记录失败与清洗后的错误()
    {
        var root = MakeTempDir("LT_TRACE_");
        try
        {
            using var memory = new SqliteTranslationMemory(MakeOptions(root));
            var run = TranslationRunContext.Create();
            var writer = new TranslationTraceWriter(root, run);
            var services = TranslationCacheServices.Create(memory, run, writer);
            var provider = MakeProvider(
                services,
                new FakeBatchClient
                {
                    ThrowOnCall = new InvalidOperationException(
                        "[错误] API 返回 500 Authorization: Bearer sk-secret"),
                });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.TranslateAsync(new[] { MakeEntry() }, CancellationToken.None, "Test.json"));

            var entry = Assert.Single(ReadTrace(writer));
            Assert.False(entry.GetProperty("success").GetBoolean());
            var summary = entry.GetProperty("errorSummary").GetString()!;
            Assert.Contains("InvalidOperationException", summary);
            Assert.DoesNotContain("sk-secret", summary);
            Assert.DoesNotContain("Bearer", summary);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Retry与耗时写入Trace()
    {
        var root = MakeTempDir("LT_TRACE_");
        try
        {
            using var memory = new SqliteTranslationMemory(MakeOptions(root));
            var run = TranslationRunContext.Create();
            var writer = new TranslationTraceWriter(root, run);
            var services = TranslationCacheServices.Create(memory, run, writer);
            var provider = MakeProvider(services, new FakeBatchClient { RetryCount = 2 });

            await provider.TranslateAsync(new[] { MakeEntry() }, CancellationToken.None, "Test.json");

            var entry = Assert.Single(ReadTrace(writer));
            Assert.Equal(2, entry.GetProperty("retryCount").GetInt32());
            Assert.Equal(7, entry.GetProperty("durationMs").GetInt64());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void 响应缺少usage_解析不失败()
    {
        var request = new[]
        {
            new DeepSeekTranslateRequestItem { Id = "A", Source = "source" },
        };
        var inner = "{\"items\":[{\"id\":\"" + DeepSeekResponseParser.EncodeId("A")
                    + "\",\"translation\":\"译文\",\"needs_review\":false,\"reason\":\"\"}]}";
        var content = "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(inner) + "}}]}";

        var parsed = DeepSeekResponseParser.ParseWithMetadata(content, request);

        Assert.Single(parsed.Items);
        Assert.Null(parsed.Metadata.ResponseId);
        Assert.Null(parsed.Metadata.PromptTokens);
    }

    [Fact]
    public void 响应包含usage_解析出元数据()
    {
        var request = new[]
        {
            new DeepSeekTranslateRequestItem { Id = "A", Source = "source" },
        };
        var inner = "{\"items\":[{\"id\":\"" + DeepSeekResponseParser.EncodeId("A")
                    + "\",\"translation\":\"译文\",\"needs_review\":false,\"reason\":\"\"}]}";
        var content = "{\"id\":\"resp-9\",\"model\":\"deepseek-v4-flash\",\"usage\":{\"prompt_tokens\":100,"
                      + "\"completion_tokens\":200,\"total_tokens\":300},"
                      + "\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(inner) + "}}]}";

        var parsed = DeepSeekResponseParser.ParseWithMetadata(content, request);

        Assert.Equal("resp-9", parsed.Metadata.ResponseId);
        Assert.Equal("deepseek-v4-flash", parsed.Metadata.ResponseModel);
        Assert.Equal(100, parsed.Metadata.PromptTokens);
        Assert.Equal(200, parsed.Metadata.CompletionTokens);
        Assert.Equal(300, parsed.Metadata.TotalTokens);
    }

    [Fact]
    public void 并发写JSONL_每行均为完整合法JSON()
    {
        var root = MakeTempDir("LT_TRACE_");
        try
        {
            var run = TranslationRunContext.Create();
            var writer = new TranslationTraceWriter(root, run);

            Parallel.For(0, 60, i => writer.Write(new TranslationTraceEntry
            {
                RunId = run.RunId,
                RequestId = $"req-{i:D5}",
                BatchId = $"Batch{i:000}",
                ItemCount = 1,
                UnitKeys = new[] { $"Test.json|{i}|a" },
                Provider = "deepseek",
                Model = "deepseek-v4-flash",
                Fingerprint = $"v1:{i}",
                Success = true,
            }));

            var lines = ReadTrace(writer);
            Assert.Equal(60, lines.Count);
            Assert.All(lines, line => Assert.True(line.TryGetProperty("requestId", out _)));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Trace中不包含APIKey()
    {
        var root = MakeTempDir("LT_TRACE_");
        try
        {
            using var memory = new SqliteTranslationMemory(MakeOptions(root));
            var run = TranslationRunContext.Create();
            var writer = new TranslationTraceWriter(root, run);
            var services = TranslationCacheServices.Create(memory, run, writer);
            var provider = MakeProvider(services, new FakeBatchClient());

            await provider.TranslateAsync(new[] { MakeEntry() }, CancellationToken.None, "Test.json");

            var raw = File.ReadAllText(writer.FilePath!);
            Assert.DoesNotContain("test-key", raw);
            Assert.DoesNotContain("Authorization", raw);
            Assert.DoesNotContain("Deal 20 damage.", raw);   // 完整源文也不写入
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Trace写入失败_翻译主流程不失败()
    {
        var root = MakeTempDir("LT_TRACE_");
        try
        {
            using var memory = new SqliteTranslationMemory(MakeOptions(root));
            var run = TranslationRunContext.Create();

            // 用一个同名“文件”占据 logs 路径，使 Trace 目录无法创建
            File.WriteAllText(Path.Combine(root, "logs"), "占位文件");

            var writer = new TranslationTraceWriter(root, run);
            var services = TranslationCacheServices.Create(memory, run, writer);
            var provider = MakeProvider(services, new FakeBatchClient());
            var entry = MakeEntry();

            var results = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");

            Assert.Single(results);
            Assert.False(results[entry.Key.ToString()].CacheHit);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
