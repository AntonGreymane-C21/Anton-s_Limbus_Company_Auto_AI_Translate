using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.IntegrationTests.Support;

/// <summary>
/// 集成测试用的 Fake 批量客户端：回显 Placeholder 保护文本（保持占位符完整），不访问网络。
/// 同时记录调用次数与最后一次请求项，供断言“真实请求里到底带了什么”。
/// </summary>
public sealed class FakeBatchClient : IDeepSeekBatchClient
{
    private readonly List<DeepSeekTranslateRequestItem> _allItems = new();
    private readonly List<(bool Enabled, string? Effort, string[] Ids)> _calls = new();
    private readonly object _gate = new();
    private int _callCount;

    public int CallCount => Volatile.Read(ref _callCount);

    public IReadOnlyList<DeepSeekTranslateRequestItem> LastItems { get; private set; } =
        Array.Empty<DeepSeekTranslateRequestItem>();

    /// <summary>本次运行所有请求项（跨批次累计，供断言上下文/占位符实际内容）。</summary>
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
        => TranslateBatchWithMetadataAsync(
            batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt,
            new DeepSeekRequestThinking(false, null));

    /// <summary>第8.75轮：记录每次请求的 Thinking 决策（自适应策略验证用）。</summary>
    public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking)
    {
        Interlocked.Increment(ref _callCount);
        LastItems = items.ToArray();
        lock (_gate)
        {
            _allItems.AddRange(items);
            _calls.Add((thinking.Enabled, thinking.ReasoningEffort, items.Select(i => i.Id).ToArray()));
        }

        var results = items.ToDictionary(
            i => i.Id,
            i => new DeepSeekTranslateItem(i.Id, "[译]" + i.Source, false, string.Empty),
            StringComparer.Ordinal);

        return Task.FromResult(new DeepSeekBatchResult
        {
            Items = results,
            ResponseId = "fixture-response",
            ResponseModel = "fixture-model",
            PromptTokens = 10,
            CompletionTokens = 20,
            TotalTokens = 30,
            RetryCount = 0,
            DurationMs = 3,
        });
    }

    /// <summary>每次请求的 Thinking 决策与条目 Id（第8.75轮）。</summary>
    public IReadOnlyList<(bool Enabled, string? Effort, string[] Ids)> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }
}

/// <summary>一次完整主链运行的产物。</summary>
public sealed record PipelineRunOutcome(
    DiffResult Diff,
    IReadOnlyList<DiffEntry> AllEntries,
    ReleaseGateResult Gate,
    OutputRunManifest Manifest,
    OutputMergeResult Output,
    int ProviderCalls,
    IReadOnlyList<TranslationContext> StoryContexts);

/// <summary>
/// 端到端主链 Harness（Parser → Diff → ContextIndex → Coordinator → Agent → TM / Context / Fingerprint / Cache
/// → Provider → Placeholder → Validator → TM Save → Merge → ReleaseGate → Manifest）。
/// </summary>
public sealed class PipelineHarness : IDisposable
{
    private readonly IntegrationFixture _fixture;
    private readonly FakeBatchClient _client = new();
    private readonly TranslationRunContext _run = TranslationRunContext.Create();

    private PipelineHarness(IntegrationFixture fixture)
    {
        _fixture = fixture;
        Memory = new SqliteTranslationMemory(new TranslationMemoryOptions { DatabasePath = fixture.TmDbPath });
        TraceWriter = new TranslationTraceWriter(fixture.TraceRoot, _run);
        CacheServices = TranslationCacheServices.Create(Memory, _run, TraceWriter);
    }

    public SqliteTranslationMemory Memory { get; }
    public TranslationTraceWriter TraceWriter { get; }
    public TranslationCacheServices CacheServices { get; }
    public string RunId => _run.RunId;
    public int ProviderCalls => _client.CallCount;
    public IReadOnlyList<DeepSeekTranslateRequestItem> LastRequestItems => _client.LastItems;
    public IReadOnlyList<DeepSeekTranslateRequestItem> AllRequestItems => _client.AllItems;
    /// <summary>每次请求的 Thinking 决策与条目 Id（第8.75轮：验证自适应策略不混合）。</summary>
    public IReadOnlyList<(bool Enabled, string? Effort, string[] Ids)> Calls => _client.Calls;

    public static PipelineHarness Create(IntegrationFixture fixture) => new(fixture);

    /// <summary>清空测试库中的 translations（保留 request_cache）：验证“TM Miss → Request Cache Hit”。</summary>
    public void ClearTranslations()
    {
        using var conn = new SqliteConnection($"Data Source={_fixture.TmDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM translations;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>统计测试库行数（translations / request_cache）。</summary>
    public int CountRows(string table)
    {
        using var conn = new SqliteConnection($"Data Source={_fixture.TmDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>执行一次完整翻译运行（真实主链，Provider 为 Fake）。</summary>
    /// <param name="indexOverride">上下文索引覆盖（默认按新版英文构建）</param>
    /// <param name="batchOptions">Provider 请求分批配置（第7.5轮；null = 默认 20 条 / 30000 字符）</param>
    public async Task<PipelineRunOutcome> RunAsync(
        TranslationContextIndex? indexOverride = null,
        BatchOptions? batchOptions = null)
    {
        var fixture = _fixture;

        // 1) Parser + DiffEngine（并返回完整 NewUnits）
        var diff = new DiffWorkflowService(fixture.ConfigDir)
            .Analyze(fixture.OldEnglishDir, fixture.OldChineseDir, fixture.NewEnglishDir, null);

        // 2) 上下文索引（基于完整新版英文；每次运行构建一次）
        var index = indexOverride ?? TranslationContextIndex.Build(diff.NewUnits);

        // 3) Provider（真实 DeepSeekTranslationProvider + Fake 客户端：走指纹 / 缓存 / Trace）
        // 第7.5轮：Provider 是唯一的「请求分批」责任方，batchOptions 直接决定实际请求条数。
        var options = new DeepSeekOptions
        {
            ApiKey = "fixture-key-value",
            ApiUrl = "https://api.deepseek.com/chat/completions",
            Model = "fixture-model",
            Temperature = 0.3,
            MaxTokens = 4096,
            MaxRetry = 0,
        };
        var provider = new DeepSeekTranslationProvider(
            options,
            fixture.ConfigDir,
            batchOptions ?? BatchOptions.Default,
            cacheServices: CacheServices,
            client: _client);

        // 4) Coordinator / TranslationAgent（Validator + Context + Cache 全部接入）
        var pipeline = ValidationPipeline.CreateDefault(fixture.ConfigDir);
        var coordinator = new Coordinator(
            provider,
            Memory,
            maxConcurrentAgents: 4,
            maxConcurrentApiRequests: 4,
            log: null,
            validation: pipeline,
            cacheServices: CacheServices,
            contextBuilder: new TranslationContextBuilder(index));

        var toTranslate = diff.Entries
            .Where(e => e.Action is TranslationAction.TranslateNew
                        or TranslationAction.TranslateModified
                        or TranslationAction.TranslateMissing)
            .ToList();

        await coordinator.ExecuteAsync(toTranslate);

        // 5) Merge + 写后校验
        var allEntries = diff.Entries.Where(e => e.Action != TranslationAction.SkipDeleted).ToList();
        var translations = Coordinator.CollectTranslations(allEntries);
        var output = new MergeOutputService().MergeAllWithReport(
            fixture.NewEnglishDir,
            translations,
            fixture.OutputDir,
            allEntries.Select(e => e.Key.ToString()));

        // 6) ReleaseGate（补齐未校验的 Inherited 条目）
        var gate = ReleaseGateService.Evaluate(allEntries, pipeline);

        // 7) Manifest
        var manifest = OutputManifestService.Save(fixture.OutputDir, output, gate);

        var contextBuilder = new TranslationContextBuilder(index);
        var storyContexts = allEntries
            .Where(e => e.Key.RelativeFilePath.StartsWith("StoryData/", StringComparison.OrdinalIgnoreCase))
            .Select(contextBuilder.Build)
            .ToList();

        return new PipelineRunOutcome(
            diff,
            allEntries,
            gate,
            manifest,
            output,
            _client.CallCount,
            storyContexts);
    }

    public void Dispose() => Memory.Dispose();
}

