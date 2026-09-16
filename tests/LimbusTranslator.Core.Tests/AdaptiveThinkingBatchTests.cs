using System.Text.Json;
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
/// 第8.75轮：自适应 Thinking 的批次分组 / 指纹 / 缓存 / Trace 测试。
///
/// 核心约束：同一个 Provider Request Batch 内不允许混合 ON 与 OFF 条目。
/// </summary>
[Collection(SqliteCollection.Name)]
public class AdaptiveThinkingBatchTests : IDisposable
{
    private readonly string _root;
    private readonly TranslationMemoryOptions _memoryOptions;
    private readonly SqliteTranslationMemory _memory;

    public AdaptiveThinkingBatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LT_ADAPTIVE_" + Guid.NewGuid().ToString("N"));
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
            // 临时目录清理可忽略
        }
    }

    /// <summary>记录每次调用的 Thinking 决策与条目 Id（覆盖第8.75轮新增重载）。</summary>
    private sealed class RecordingThinkingClient : IDeepSeekBatchClient
    {
        private readonly List<(bool Enabled, string? Effort, List<string> Ids)> _calls = new();

        public IReadOnlyList<(bool Enabled, string? Effort, List<string> Ids)> Calls => _calls;

        public int CallCount => _calls.Count;

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
            => TranslateBatchWithMetadataAsync(
                batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt,
                new DeepSeekRequestThinking(false, null));

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt,
            DeepSeekRequestThinking thinking)
        {
            _calls.Add((thinking.Enabled, thinking.ReasoningEffort, items.Select(i => i.Id).ToList()));

            var results = items.ToDictionary(
                i => i.Id,
                i => new DeepSeekTranslateItem(i.Id, "[译]" + i.Source, false, string.Empty),
                StringComparer.Ordinal);
            return Task.FromResult(new DeepSeekBatchResult
            {
                Items = results,
                PromptTokens = 10,
                CompletionTokens = 20,
                TotalTokens = 30,
            });
        }
    }

    private static DiffEntry Entry(string file, string recordId, string fieldPath, string source)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = recordId, FieldPath = fieldPath },
            NewSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };

    /// <summary>ON/OFF 交错输入：2 个 OFF 普通文本 + 3 个 ON（StoryData ×2 / 韩文 ×1）。</summary>
    private static List<DiffEntry> MixedEntries() => new()
    {
        Entry("Skills.json", "0", "dataList[0].levelList[0].name", "Skill name one."),
        Entry("StoryData/S949A.json", "1", "dataList[1].content", "A story line."),
        Entry("Skills.json", "1", "dataList[1].levelList[0].desc", "Skill desc two."),
        Entry("BattleSpeechBubbleDlg.json", "2", "dataList[2].desc", "말풍선 특수 대사_크로머"),
        Entry("StoryData/S949A.json", "3", "dataList[3].content", "Another story line."),
    };

    private async Task<(IReadOnlyDictionary<string, TranslationResult> Results, RecordingThinkingClient Client,
        IReadOnlyList<JsonElement> Trace)> RunAsync(TranslationThinkingPolicy? policy = null)
    {
        var run = TranslationRunContext.Create();
        var trace = new TranslationTraceWriter(_root, run);
        var services = TranslationCacheServices.Create(_memory, run, trace);
        var client = new RecordingThinkingClient();
        var provider = new DeepSeekTranslationProvider(
            new DeepSeekOptions
            {
                ApiKey = "sk-test",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "m",
                MaxRetry = 0,
            },
            configDir: null,
            cacheServices: services,
            client: client,
            thinkingPolicy: policy);

        var entries = MixedEntries();
        var results = await provider.TranslateAsync(entries);
        if (services.Staging is not null)
        {
            var requestIds = results.Values
                .Select(r => r.RequestId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .Distinct()
                .ToArray();
            services.Staging.Flush(requestIds);
        }

        provider.Dispose();

        var traceLines = trace.FilePath is not null && File.Exists(trace.FilePath)
            ? File.ReadLines(trace.FilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonDocument.Parse(l).RootElement.Clone())
                .ToList()
            : new List<JsonElement>();
        return (results, client, traceLines);
    }

    [Fact]
    public async Task 混合输入_不得产生混合Thinking的请求()
    {
        var (_, client, _) = await RunAsync();

        Assert.Equal(2, client.CallCount);
        // 分组顺序 = 首次出现顺序：第 1 条是 OFF ⇒ OFF 组先发；组内保持原顺序
        Assert.Equal(new[] { false, true }, client.Calls.Select(c => c.Enabled).ToArray());
        Assert.Equal(2, client.Calls[0].Ids.Count);   // Skills ×2
        Assert.Equal(3, client.Calls[1].Ids.Count);   // StoryData ×2 + 韩文 ×1

        Assert.DoesNotContain(client.Calls[0].Ids, id => id.StartsWith("StoryData/"));
        Assert.DoesNotContain(client.Calls[0].Ids, id => id.StartsWith("BattleSpeechBubbleDlg.json"));
        Assert.Contains(client.Calls[1].Ids, id => id.StartsWith("StoryData/"));
        Assert.Contains(client.Calls[1].Ids, id => id.StartsWith("BattleSpeechBubbleDlg.json"));
    }

    [Fact]
    public async Task ON与OFF_指纹不同_且重复运行稳定()
    {
        var (first, _, _) = await RunAsync();
        var (second, _, _) = await RunAsync();

        var onFingerprint = first.Values.Single(r => r.Translation == "[译]A story line.").RequestFingerprint;
        var offFingerprint = first.Values.Single(r => r.Translation == "[译]Skill name one.").RequestFingerprint;

        Assert.NotNull(onFingerprint);
        Assert.NotNull(offFingerprint);
        Assert.NotEqual(onFingerprint, offFingerprint);

        Assert.Equal(
            first.Values.Select(r => r.RequestFingerprint).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            second.Values.Select(r => r.RequestFingerprint).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task 缓存_OFF不得命中ON的缓存()
    {
        var (_, firstClient, _) = await RunAsync();
        Assert.Equal(2, firstClient.CallCount);   // 自适应：OFF 组 + ON 组

        // 强制 OFF：全部条目变成一个 OFF 组（指纹与 ON 组不同）⇒ 必须重新调用，ON 缓存不得被命中
        var (_, secondClient, _) = await RunAsync(new TranslationThinkingPolicy(TranslationThinkingMode.AlwaysOff));

        Assert.Equal(1, secondClient.CallCount);
        Assert.All(secondClient.Calls, c => Assert.False(c.Enabled));
    }

    [Fact]
    public async Task Trace_记录ThinkingEnabled与决策原因()
    {
        var (_, _, trace) = await RunAsync();

        Assert.Equal(2, trace.Count);
        var offLine = trace.Single(t => t.GetProperty("thinkingEnabled").GetBoolean() == false);
        var onLine = trace.Single(t => t.GetProperty("thinkingEnabled").GetBoolean() == true);

        Assert.Equal(ThinkingPolicyReasons.DefaultOff, offLine.GetProperty("thinkingPolicyReason").GetString());
        Assert.Equal(2, offLine.GetProperty("itemCount").GetInt32());
        Assert.Equal(3, onLine.GetProperty("itemCount").GetInt32());
        Assert.Contains(
            onLine.GetProperty("thinkingPolicyReason").GetString(),
            new[] { ThinkingPolicyReasons.StoryData, ThinkingPolicyReasons.SourceLanguageAnomaly });
    }

    [Fact]
    public async Task 空源文被Provider过滤_纯符号由Agent层拦截()
    {
        var run = TranslationRunContext.Create();
        var services = TranslationCacheServices.Create(_memory, run);
        var client = new RecordingThinkingClient();
        var provider = new DeepSeekTranslationProvider(
            new DeepSeekOptions
            {
                ApiKey = "sk-test",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "m",
                MaxRetry = 0,
            },
            configDir: null,
            cacheServices: services,
            client: client);

        var entries = new List<DiffEntry>
        {
            Entry("Skills.json", "9", "dataList[9].name", "???"),
            Entry("Skills.json", "10", "dataList[10].name", string.Empty),
            Entry("Skills.json", "11", "dataList[11].name", "Real plain english."),
        };

        await provider.TranslateAsync(entries);
        provider.Dispose();

        // Provider 的第7轮 fail-safe 只过滤空源文；纯符号由 TranslationAgent 层直通（见 SymbolPassthroughTests）
        Assert.Equal(1, client.CallCount);
        Assert.Equal(2, client.Calls[0].Ids.Count);
        Assert.False(client.Calls[0].Enabled);
    }
}
