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
/// 第8.5轮：Thinking 参数真实进入请求 + reasoning token 统计。
///
/// 覆盖：
///   - 请求体 JSON 是否真的表达 thinking / reasoning_effort（不访问网络）；
///   - 指纹是否随 Thinking 变化、不同 Thinking 是否互相隔离缓存；
///   - usage.completion_tokens_details.reasoning_tokens 的解析与 VisibleOutputTokens 计算；
///   - Trace 字段；旧缓存缺字段时的向后兼容。
/// </summary>
[Collection(SqliteCollection.Name)]
public class ThinkingAndReasoningTokenTests : IDisposable
{
    private readonly string _root;
    private readonly TranslationMemoryOptions _memoryOptions;
    private readonly SqliteTranslationMemory _memory;

    public ThinkingAndReasoningTokenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LT_THINK_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _memoryOptions = new TranslationMemoryOptions { DatabasePath = Path.Combine(_root, "tm.db") };
        _memory = new SqliteTranslationMemory(_memoryOptions);
    }

    public void Dispose()
    {
        _memory.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // 临时目录清理忽略
        }
    }

    private static DeepSeekOptions Options(bool thinking, string? effort = null) => new()
    {
        ApiKey = "sk-thinking-test",
        ApiUrl = "https://api.deepseek.com/chat/completions",
        Model = "test-model",
        Thinking = thinking,
        ReasoningEffort = effort,
        MaxRetry = 0,
    };

    private static IReadOnlyList<DeepSeekTranslateRequestItem> Items()
        => new[] { new DeepSeekTranslateRequestItem { Id = "Test.json|1|dataList[0].content", Source = "Hello" } };

    private static DiffEntry MakeEntry(string source = "Hello world.")
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
            NewSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };

    [Fact]
    public void 请求体_Thinking关闭_发送disabled且不含reasoning_effort()
    {
        var json = DeepSeekRequestComposer.BuildRequestBodyJson(
            Options(thinking: false, effort: "high"), new PromptOptions(), "Batch001", Items(), string.Empty, string.Empty);

        Assert.Contains("\"thinking\":{\"type\":\"disabled\"}", json);
        Assert.DoesNotContain("reasoning_effort", json);
    }

    [Fact]
    public void 请求体_Thinking开启_发送enabled与reasoning_effort()
    {
        var json = DeepSeekRequestComposer.BuildRequestBodyJson(
            Options(thinking: true, effort: "high"), new PromptOptions(), "Batch001", Items(), string.Empty, string.Empty);

        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", json);
        Assert.Contains("\"reasoning_effort\":\"high\"", json);
    }

    [Fact]
    public void 请求体_Thinking开启但未配置强度_只发送开关()
    {
        var json = DeepSeekRequestComposer.BuildRequestBodyJson(
            Options(thinking: true), new PromptOptions(), "Batch001", Items(), string.Empty, string.Empty);

        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", json);
        Assert.DoesNotContain("reasoning_effort", json);
    }

    /// <summary>回显请求的假客户端（不访问网络），可选返回 reasoning token。</summary>
    private sealed class EchoClient : IDeepSeekBatchClient
    {
        public int CallCount { get; private set; }

        public int? ReasoningTokens { get; init; }

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
        {
            CallCount++;
            var results = items.ToDictionary(
                i => i.Id,
                i => new DeepSeekTranslateItem(i.Id, "[译]" + i.Source, false, string.Empty),
                StringComparer.Ordinal);
            return Task.FromResult(new DeepSeekBatchResult
            {
                Items = results,
                PromptTokens = 100,
                CompletionTokens = 60,
                TotalTokens = 160,
                ReasoningTokens = ReasoningTokens,
            });
        }
    }

    private async Task<(TranslationResult Result, EchoClient Client, TranslationCacheServices Services, TranslationRunContext Run)>
        RunAsync(bool thinking, string? effort, EchoClient client, TranslationCacheServices? services = null)
    {
        var run = TranslationRunContext.Create();
        var effective = services ?? TranslationCacheServices.Create(_memory, run);
        var provider = new DeepSeekTranslationProvider(
            Options(thinking, effort), configDir: null, batchOptions: null, cacheServices: effective, client: client);

        var entry = MakeEntry();
        var results = await provider.TranslateAsync(new[] { entry });
        provider.Dispose();
        return (results[entry.Key.ToString()], client, effective, run);
    }

    [Fact]
    public async Task 指纹_Thinking开关不同则不同()
    {
        var on = await RunAsync(thinking: true, effort: "high", new EchoClient());
        var off = await RunAsync(thinking: false, effort: null, new EchoClient());

        Assert.NotNull(on.Result.RequestFingerprint);
        Assert.NotNull(off.Result.RequestFingerprint);
        Assert.NotEqual(on.Result.RequestFingerprint, off.Result.RequestFingerprint);
    }

    [Fact]
    public async Task 缓存_Thinking关闭后不得命中Thinking开启写下的缓存()
    {
        var first = await RunAsync(thinking: true, effort: "high", new EchoClient());
        first.Services.Staging!.Flush(new[] { first.Result.RequestId! });
        Assert.Equal(1, CountRows("request_cache"));

        // 第二次：相同文本但 thinking=false → 请求不同 → 必须重新调用客户端
        var offClient = new EchoClient();
        await RunAsync(thinking: false, effort: null, offClient);

        Assert.Equal(1, offClient.CallCount);
    }

    [Fact]
    public async Task 缓存_相同Thinking可命中且不调用客户端()
    {
        var first = await RunAsync(thinking: false, effort: null, new EchoClient());
        first.Services.Staging!.Flush(new[] { first.Result.RequestId! });

        var second = await RunAsync(thinking: false, effort: null, new EchoClient());

        Assert.Equal(0, second.Client.CallCount);
        Assert.True(second.Result.CacheHit);
    }

    [Fact]
    public void 响应含reasoning_tokens_被正确解析()
    {
        var json = """
            {"id":"resp-1","model":"m","choices":[{"message":{"content":"{\"items\":[{\"id\":\"A\",\"translation\":\"甲\",\"needs_review\":false,\"reason\":\"\"}]}"}}],
             "usage":{"prompt_tokens":100,"completion_tokens":60,"total_tokens":160,
                      "completion_tokens_details":{"reasoning_tokens":45}}}
            """;
        var request = new[] { new DeepSeekTranslateRequestItem { Id = "A", Source = "s" } };

        var (_, metadata) = DeepSeekResponseParser.ParseWithMetadata(json, request);

        Assert.Equal(45, metadata.ReasoningTokens);
        Assert.Equal(60, metadata.CompletionTokens);
        Assert.Equal(15, metadata.VisibleOutputTokens);
    }

    [Fact]
    public void 响应缺失reasoning_tokens_为null且不失败()
    {
        var json = """
            {"id":"resp-1","model":"m","choices":[{"message":{"content":"{\"items\":[{\"id\":\"A\",\"translation\":\"甲\",\"needs_review\":false,\"reason\":\"\"}]}"}}],
             "usage":{"prompt_tokens":100,"completion_tokens":60,"total_tokens":160}}
            """;
        var request = new[] { new DeepSeekTranslateRequestItem { Id = "A", Source = "s" } };

        var (items, metadata) = DeepSeekResponseParser.ParseWithMetadata(json, request);

        Assert.Single(items);
        Assert.Null(metadata.ReasoningTokens);
        Assert.Null(metadata.VisibleOutputTokens);
    }

    private int CountRows(string table)
    {
        using var conn = new SqliteConnection($"Data Source={_memoryOptions.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
