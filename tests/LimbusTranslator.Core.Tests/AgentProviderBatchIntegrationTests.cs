using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Translation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第7.5轮：Agent ↔ Provider ↔ request_cache ↔ Trace 的分批收口测试。
///
/// 目标：
///   1. 证明「Provider Request Batch」只由 DeepSeekTranslationProvider 切分一次（Agent 不再分批）；
///   2. 证明分批配置真实生效（maxItems=10 → 10/10/5），并影响指纹与缓存；
///   3. 证明 Trace 记录的是实际 Provider 批次；
///   4. 证明分批不改变 Neighbor Context。
///
/// 全部使用临时 SQLite / 临时 Trace / Fake 客户端，不访问真实 API。
/// </summary>
[Collection(SqliteCollection.Name)]
public class AgentProviderBatchIntegrationTests : IDisposable
{
    private readonly string _root;
    private readonly TranslationMemoryOptions _memoryOptions;
    private readonly SqliteTranslationMemory _memory;

    public AgentProviderBatchIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LT_BATCH_IT_" + Guid.NewGuid().ToString("N"));
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
            // 临时目录清理失败可忽略
        }
    }

    /// <summary>记录每次实际收到的条目数与 UnitKey（可用于证明"谁在分批"）。</summary>
    private sealed class RecordingBatchClient : IDeepSeekBatchClient
    {
        private readonly List<int> _counts = new();
        private readonly List<List<string>> _ids = new();
        private readonly List<DeepSeekTranslateRequestItem> _allItems = new();
        private readonly object _gate = new();

        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _counts.Count;
                }
            }
        }

        public IReadOnlyList<int> CallItemCounts
        {
            get
            {
                lock (_gate)
                {
                    return _counts.ToArray();
                }
            }
        }

        public IReadOnlyList<IReadOnlyList<string>> CallIds
        {
            get
            {
                lock (_gate)
                {
                    return _ids.Select(list => (IReadOnlyList<string>)list.ToArray()).ToArray();
                }
            }
        }

        public IReadOnlyList<DeepSeekTranslateRequestItem> AllItems
        {
            get
            {
                lock (_gate)
                {
                    return _allItems.ToArray();
                }
            }
        }

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
        {
            lock (_gate)
            {
                _counts.Add(items.Count);
                _ids.Add(items.Select(i => i.Id).ToList());
                _allItems.AddRange(items);
            }

            var results = items.ToDictionary(
                i => i.Id,
                i => new DeepSeekTranslateItem(i.Id, "[译]" + i.Source, false, string.Empty),
                StringComparer.Ordinal);
            return Task.FromResult(new DeepSeekBatchResult { Items = results });
        }
    }

    /// <summary>记录每次调用收到多少条目的业务层 Provider（证明 Agent 层不再分批）。</summary>
    private sealed class RecordingProvider : ITranslationProvider
    {
        public List<int> CallSizes { get; } = new();

        public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
            IReadOnlyList<DiffEntry> entries,
            CancellationToken cancellationToken = default,
            string? stageId = null,
            IReadOnlyDictionary<string, TranslationContext>? contexts = null)
        {
            CallSizes.Add(entries.Count);

            var results = entries.ToDictionary(
                e => e.Key.ToString(),
                e => new TranslationResult
                {
                    Key = e.Key,
                    Translation = "[AI]" + e.NewSourceText,
                    Source = TranslationSource.AI,
                },
                StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, TranslationResult>>(results);
        }
    }

    private sealed record BatchRun(
        AgentExecutionResult AgentResult,
        RecordingBatchClient Client,
        string RunId,
        IReadOnlyList<JsonElement> TraceLines);

    // ---------------- 测试 ----------------

    [Fact]
    public async Task Agent不再分批_25条一次性交给Provider()
    {
        var provider = new RecordingProvider();
        var agent = new TranslationAgent(provider, new RateLimitManager(4, 4), _memory);

        var result = await agent.ExecuteAsync("Test.json", MakeEntries(25));

        Assert.True(result.IsSuccess);
        Assert.Equal(25, result.TranslatedCount);
        // 若 Agent 仍在做 20 条切分，这里会是 [20, 5]
        Assert.Equal(new[] { 25 }, provider.CallSizes);
    }

    [Fact]
    public async Task Provider独占分批_25条maxItems10_实际请求为10_10_5()
    {
        var run = await RunBatchAsync(MakeEntries(25), new BatchOptions { MaxItemsPerBatch = 10 });

        Assert.Equal(3, run.Client.CallCount);
        Assert.Equal(new[] { 10, 10, 5 }, run.Client.CallItemCounts);
        Assert.Equal(25, run.AgentResult.TranslatedCount);
    }

    [Fact]
    public async Task 分批写入缓存_第二次相同配置全部命中且不调用客户端()
    {
        var options = new BatchOptions { MaxItemsPerBatch = 10 };

        var first = await RunBatchAsync(MakeEntries(25), options);
        Assert.Equal(3, first.Client.CallCount);
        Assert.Equal(3, CountRows("request_cache"));

        var second = await RunBatchAsync(MakeEntries(25), options, clearTranslationsFirst: true);

        Assert.Equal(0, second.Client.CallCount);                  // 全部来自 request_cache
        Assert.Equal(3, second.TraceLines.Count);
        Assert.All(second.TraceLines, l => Assert.True(l.GetProperty("cacheHit").GetBoolean()));
        Assert.All(second.TraceLines, l => Assert.False(l.GetProperty("networkCalled").GetBoolean()));
        Assert.Equal(3, CountRows("request_cache"));               // 相同指纹不重复增长
    }

    [Fact]
    public async Task 分批配置改变_不错误复用旧缓存()
    {
        var first = await RunBatchAsync(MakeEntries(25), new BatchOptions { MaxItemsPerBatch = 10 });
        Assert.Equal(3, first.Client.CallCount);
        Assert.Equal(3, CountRows("request_cache"));

        var second = await RunBatchAsync(
            MakeEntries(25), new BatchOptions { MaxItemsPerBatch = 5 }, clearTranslationsFirst: true);

        Assert.Equal(new[] { 5, 5, 5, 5, 5 }, second.Client.CallItemCounts);   // 新组合 → 全部 Miss
        Assert.Equal(8, CountRows("request_cache"));                          // 3 + 5，旧的 10/10/5 未被误命中
    }

    [Fact]
    public async Task Trace反映实际Provider批次()
    {
        var run = await RunBatchAsync(MakeEntries(25), new BatchOptions { MaxItemsPerBatch = 10 });

        Assert.Equal(3, run.TraceLines.Count);
        Assert.Equal(
            new[] { 10, 10, 5 },
            run.TraceLines.Select(l => l.GetProperty("itemCount").GetInt32()).ToArray());
        Assert.All(run.TraceLines, l => Assert.Equal(run.RunId, l.GetProperty("runId").GetString()));
        Assert.Equal(3, run.TraceLines.Select(l => l.GetProperty("requestId").GetString()).Distinct().Count());
        Assert.Equal(3, run.TraceLines.Select(l => l.GetProperty("fingerprint").GetString()).Distinct().Count());

        for (var i = 0; i < 3; i++)
        {
            var traceKeys = run.TraceLines[i].GetProperty("unitKeys")
                .EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.Equal(run.Client.CallIds[i], traceKeys);
        }
    }

    [Fact]
    public async Task 相同配置重复运行_批次边界与指纹一致()
    {
        var options = new BatchOptions { MaxItemsPerBatch = 7 };

        var first = await RunBatchAsync(MakeEntries(20), options);
        var second = await RunBatchAsync(MakeEntries(20), options, clearTranslationsFirst: true);

        Assert.Equal(new[] { 7, 7, 6 }, first.Client.CallItemCounts);
        Assert.Empty(second.Client.CallItemCounts);                 // 完全相同请求 → 全部命中缓存

        var firstFingerprints = first.TraceLines.Select(l => l.GetProperty("fingerprint").GetString()).ToArray();
        var secondFingerprints = second.TraceLines.Select(l => l.GetProperty("fingerprint").GetString()).ToArray();
        Assert.Equal(firstFingerprints, secondFingerprints);
    }

    [Fact]
    public async Task 分批不改变NeighborContext()
    {
        var units = new List<TranslationUnit>
        {
            MakeUnit(0, "Line zero."),
            MakeUnit(1, "Line one."),
            MakeUnit(2, "Line two."),
        };
        var builder = new TranslationContextBuilder(TranslationContextIndex.Build(units));
        var entries = units
            .Select(u => MakeEntry(int.Parse(u.RecordId), u.SourceText, "StoryData/1D101A.json"))
            .ToList();

        var wide = await RunBatchAsync(
            entries, new BatchOptions { MaxItemsPerBatch = 20 }, clearTranslationsFirst: true, contextBuilder: builder);
        var narrow = await RunBatchAsync(
            entries, new BatchOptions { MaxItemsPerBatch = 1 }, clearTranslationsFirst: true, contextBuilder: builder);

        Assert.Equal(new[] { 3 }, wide.Client.CallItemCounts);
        Assert.Equal(new[] { 1, 1, 1 }, narrow.Client.CallItemCounts);   // 分批只改变请求组合

        var targetId = entries[1].Key.ToString();
        var wideItem = wide.Client.AllItems.Single(i => i.Id == targetId);
        var narrowItem = narrow.Client.AllItems.Single(i => i.Id == targetId);

        Assert.Equal("Line zero.", wideItem.Context!.Previous!.SourceText);
        Assert.Equal("Line two.", wideItem.Context!.Next!.SourceText);
        Assert.Equal(wideItem.Context!.Previous!.SourceText, narrowItem.Context!.Previous!.SourceText);
        Assert.Equal(wideItem.Context!.Next!.SourceText, narrowItem.Context!.Next!.SourceText);
    }

    private static TranslationUnit MakeUnit(int index, string source)
        => new()
        {
            Key = new UnitKey
            {
                RelativeFilePath = "StoryData/1D101A.json",
                RecordId = index.ToString(),
                FieldPath = $"dataList[{index}].content",
            },
            FilePath = "StoryData/1D101A.json",
            RecordId = index.ToString(),
            FieldPath = $"dataList[{index}].content",
            SourceText = source,
            Order = index,
        };

    // ---------------- 助手 ----------------

    private static DiffEntry MakeEntry(int index, string? source = null, string file = "Test.json")
        => new()
        {
            Key = new UnitKey
            {
                RelativeFilePath = file,
                RecordId = index.ToString(),
                FieldPath = $"dataList[{index}].content",
            },
            NewSourceText = source ?? $"Sentence {index} needs translation.",
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
            Order = index,
        };

    private static List<DiffEntry> MakeEntries(int count)
        => Enumerable.Range(0, count).Select(i => MakeEntry(i)).ToList();

    /// <summary>执行一次完整的 Agent → Provider → 缓存 / Trace 运行（每次独立 RunId）。</summary>
    private async Task<BatchRun> RunBatchAsync(
        IReadOnlyList<DiffEntry> entries,
        BatchOptions options,
        bool clearTranslationsFirst = false,
        ITranslationContextBuilder? contextBuilder = null)
    {
        if (clearTranslationsFirst)
        {
            ClearTranslations();
        }

        var run = TranslationRunContext.Create();
        var trace = new TranslationTraceWriter(_root, run);
        var services = TranslationCacheServices.Create(_memory, run, trace);
        var client = new RecordingBatchClient();
        var provider = new DeepSeekTranslationProvider(
            new DeepSeekOptions
            {
                ApiKey = "sk-batch-it",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "test-model",
                MaxRetry = 0,
            },
            configDir: null,
            batchOptions: options,
            cacheServices: services,
            client: client);

        var agent = new TranslationAgent(
            provider,
            new RateLimitManager(4, 4),
            _memory,
            cacheServices: services,
            contextBuilder: contextBuilder);

        var result = await agent.ExecuteAsync("Test.json", entries);
        provider.Dispose();

        return new BatchRun(result, client, run.RunId, ReadTrace(trace));
    }

    private void ClearTranslations()
    {
        using var conn = new SqliteConnection($"Data Source={_memoryOptions.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM translations;";
        cmd.ExecuteNonQuery();
    }

    private int CountRows(string table)
    {
        using var conn = new SqliteConnection($"Data Source={_memoryOptions.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static IReadOnlyList<JsonElement> ReadTrace(TranslationTraceWriter writer)
        => writer.FilePath is not null && File.Exists(writer.FilePath)
            ? File.ReadAllLines(writer.FilePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList()
            : Array.Empty<JsonElement>();
}
