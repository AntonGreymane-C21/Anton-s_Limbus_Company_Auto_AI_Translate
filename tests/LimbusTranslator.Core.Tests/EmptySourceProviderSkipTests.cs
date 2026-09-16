using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Translation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第7轮（T-5）：空 SourceText 必须在翻译队列入口被拦截。
///
/// 规则：
///   - 空 / 纯空白源文永远不调用 Provider，也不写 TM / request_cache / Trace；
///   - 旧中文非空时保留继承语义（不得把已有旧中文清空）；
///   - 旧中文为空时译文维持空串（Merge 需要该 UnitKey 存在）。
/// </summary>
[Collection(SqliteCollection.Name)]
public class EmptySourceProviderSkipTests : IDisposable
{
    private readonly string _root;
    private readonly TranslationMemoryOptions _memoryOptions;
    private readonly SqliteTranslationMemory _memory;
    private readonly TranslationTraceWriter _trace;
    private readonly TranslationCacheServices _cacheServices;

    public EmptySourceProviderSkipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LT_EMPTY_SRC_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _memoryOptions = new TranslationMemoryOptions { DatabasePath = Path.Combine(_root, "tm.db") };
        _memory = new SqliteTranslationMemory(_memoryOptions);
        _trace = new TranslationTraceWriter(_root, TranslationRunContext.Create());
        _cacheServices = TranslationCacheServices.Create(_memory, TranslationRunContext.Create(), _trace);
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

    /// <summary>记录调用次数与批内条目的测试 Provider（不访问网络）。</summary>
    private sealed class CountingProvider : ITranslationProvider
    {
        private readonly List<DiffEntry> _seen = new();

        public int CallCount { get; private set; }

        public IReadOnlyList<DiffEntry> Seen => _seen;

        public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
            IReadOnlyList<DiffEntry> entries,
            CancellationToken cancellationToken = default,
            string? stageId = null,
            IReadOnlyDictionary<string, TranslationContext>? contexts = null)
        {
            CallCount++;
            _seen.AddRange(entries);

            var results = new Dictionary<string, TranslationResult>();
            foreach (var entry in entries)
            {
                results[entry.Key.ToString()] = new TranslationResult
                {
                    Key = entry.Key,
                    Translation = "[AI]" + entry.NewSourceText,
                    Source = TranslationSource.AI,
                };
            }

            return Task.FromResult<IReadOnlyDictionary<string, TranslationResult>>(results);
        }
    }

    private static DiffEntry MakeEntry(
        string fieldPath,
        string? source,
        string? oldTranslation = null,
        string recordId = "1",
        TranslationAction action = TranslationAction.TranslateModified)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Enemies.json", RecordId = recordId, FieldPath = fieldPath },
            NewSourceText = source,
            OldSourceText = "old english",
            OldTranslation = oldTranslation,
            DiffKind = DiffKind.Modified,
            Action = action,
        };

    private Task<AgentExecutionResult> RunAgentAsync(CountingProvider provider, params DiffEntry[] entries)
    {
        var agent = new TranslationAgent(
            provider,
            new RateLimitManager(4, 4),
            _memory,
            cacheServices: _cacheServices);
        return agent.ExecuteAsync("Enemies.json", entries);
    }

    private int CountRows(string table)
    {
        using var conn = new SqliteConnection($"Data Source={_memoryOptions.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int TraceLineCount()
        => _trace.FilePath is not null && File.Exists(_trace.FilePath)
            ? File.ReadLines(_trace.FilePath).Count(l => !string.IsNullOrWhiteSpace(l))
            : 0;

    [Fact]
    public async Task 空源文且旧中文非空_保留旧中文且不调用Provider()
    {
        var entry = MakeEntry("dataList[0].desc", string.Empty, oldTranslation: "既有旧中文");
        var provider = new CountingProvider();

        var result = await RunAgentAsync(provider, entry);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, provider.CallCount);                              // 不调用 Provider
        Assert.Equal("既有旧中文", entry.Translation);                     // 旧中文没有被清空
        Assert.Equal(TranslationSource.Inherited, entry.Provenance);
        Assert.Equal(TranslationMemoryMatchType.None, entry.TmMatchType);
        Assert.False(entry.NeedsReview);
    }

    [Fact]
    public async Task 空源文且旧中文为空_不调用Provider且译文维持空串()
    {
        var entry = MakeEntry("dataList[1].flavor", string.Empty);
        var provider = new CountingProvider();

        var result = await RunAgentAsync(provider, entry);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(string.Empty, entry.Translation);                    // 空串（不是 null，Merge 需要该 key）
        Assert.Null(entry.Provenance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public async Task 空白源文_一律不调用Provider(string blank)
    {
        var entry = MakeEntry("dataList[2].desc", blank, oldTranslation: "旧中文保留");
        var provider = new CountingProvider();

        await RunAgentAsync(provider, entry);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal("旧中文保留", entry.Translation);
    }

    [Fact]
    public async Task 空源文_不写TM_不写requestCache_不生成Trace()
    {
        var entries = new[]
        {
            MakeEntry("dataList[0].desc", string.Empty, oldTranslation: "旧中文"),
            MakeEntry("dataList[1].flavor", string.Empty, recordId: "2"),
        };
        var provider = new CountingProvider();

        await RunAgentAsync(provider, entries);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, CountRows("translations"));
        Assert.Equal(0, CountRows("request_cache"));
        Assert.Equal(0, TraceLineCount());
    }

    [Fact]
    public async Task 非空源文_行为保持不变()
    {
        var entry = MakeEntry("dataList[0].desc", "Real english text.");
        var provider = new CountingProvider();

        var result = await RunAgentAsync(provider, entry);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal("[AI]Real english text.", entry.Translation);
        Assert.Equal(TranslationSource.AI, entry.Provenance);
        Assert.Equal(1, CountRows("translations"));
    }

    /// <summary>回显请求的假批量客户端（不访问网络）。</summary>
    private sealed class EchoBatchClient : IDeepSeekBatchClient
    {
        public int CallCount { get; private set; }

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
            return Task.FromResult(new DeepSeekBatchResult { Items = results });
        }
    }

    private DeepSeekTranslationProvider MakeProvider(EchoBatchClient client)
        => new(
            new DeepSeekOptions
            {
                ApiKey = "sk-empty-source-test",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "test-model",
                MaxRetry = 0,
            },
            configDir: null,
            cacheServices: _cacheServices,
            client: client);

    [Fact]
    public async Task DeepSeekProvider_空源文_不生成请求且不写缓存与Trace()
    {
        var client = new EchoBatchClient();
        var provider = MakeProvider(client);
        var entry = MakeEntry("dataList[0].desc", string.Empty, oldTranslation: "旧中文");

        var results = await provider.TranslateAsync(new[] { entry });

        Assert.Empty(results);                          // 没有请求项
        Assert.Equal(0, client.CallCount);              // 未调用 API
        Assert.Equal(0, CountRows("translations"));
        Assert.Equal(0, CountRows("request_cache"));
        Assert.Equal(0, TraceLineCount());
        provider.Dispose();
    }

    [Fact]
    public async Task DeepSeekProvider_非空源文_正常请求并写缓存与Trace()
    {
        var client = new EchoBatchClient();
        var provider = MakeProvider(client);
        var entry = MakeEntry("dataList[0].desc", "Real english text.");

        var results = await provider.TranslateAsync(new[] { entry });
        var result = results[entry.Key.ToString()];

        Assert.Equal(1, client.CallCount);
        Assert.Equal("[译]Real english text.", result.Translation);
        Assert.Equal(1, TraceLineCount());              // Trace 立即写入

        // 缓存是两阶段：必须由 Agent 校验通过后 Flush 才可命中
        Assert.Equal(0, CountRows("request_cache"));
        _cacheServices.Staging!.Flush(new[] { result.RequestId! });
        Assert.Equal(1, CountRows("request_cache"));
        provider.Dispose();
    }

    [Fact]
    public async Task 混合条目_只有非空条目进入Provider批次()
    {
        var blankWithOld = MakeEntry("dataList[0].desc", string.Empty, oldTranslation: "保留旧中文");
        var blank = MakeEntry("dataList[1].flavor", "  ", recordId: "2");
        var normal = MakeEntry("dataList[2].name", "Translate me.", recordId: "3");

        var provider = new CountingProvider();
        await RunAgentAsync(provider, blankWithOld, blank, normal);

        Assert.Equal(1, provider.CallCount);
        Assert.Single(provider.Seen);
        Assert.Equal(normal.Key.ToString(), provider.Seen[0].Key.ToString());
        Assert.Equal("[AI]Translate me.", normal.Translation);
        Assert.Equal("保留旧中文", blankWithOld.Translation);
        Assert.Equal(string.Empty, blank.Translation);
        Assert.Equal(1, CountRows("translations"));
    }
}
