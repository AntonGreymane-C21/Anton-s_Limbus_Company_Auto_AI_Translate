using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Translation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.5轮：**空响应 / 批级失败隔离** 回归。
///
/// 真实背景：kr_en + 思考(always_on/high) 的批次请求 ~36 秒后失败、重试 6 次（共 279 秒），
/// 旧行为是「一个批失败 ⇒ 整个 stage 失败 ⇒ 该文件 150 条全无译文 ⇒ 整文件不输出 ⇒ 门禁 Blocked」。
/// 现改为：关闭思考再试一次；仍失败则标记待审并继续其它批次（配合 Merge「保留原文写入」与门禁非阻断提示）。
/// </summary>
public sealed class BatchFailureIsolationTests
{
    private static DiffEntry Entry(string recordId, string source)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Items.json", RecordId = recordId, FieldPath = "dataList[0].name" },
            NewSourceText = source,
            DiffKind = DiffKind.Modified,
            Action = TranslationAction.TranslateModified,
        };

    // 注意：每个用例都必须新建条目（批级隔离会把条目 NeedsReview 置为 true，
    // 共用静态条目会造成用例间污染——这正是本轮测试第一次跑出来的假失败）。
    private static IReadOnlyList<DiffEntry> CreateEntries()
        => new[] { Entry("1", "Hello A"), Entry("2", "Hello B") };

    private static DeepSeekTranslationProvider CreateProvider(IDeepSeekBatchClient client, Action<string>? log = null)
        => new(
            new DeepSeekOptions
            {
                ApiKey = "sk-test",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "m",
                MaxRetry = 0,
            },
            configDir: null,
            batchOptions: null,
            cacheServices: null,
            client: client,
            log: log,
            thinkingPolicy: new TranslationThinkingPolicy(TranslationThinkingMode.AlwaysOn, "high"));

    private static List<DeepSeekTranslateRequestItem> RequestItems()
        => CreateEntries()
            .Select(entry => new DeepSeekTranslateRequestItem { Id = entry.Key.ToString(), Source = entry.NewSourceText ?? string.Empty })
            .ToList();

    // ───────── ① 空响应必须给出可读中文原因（不再只抛英文 JSON 异常） ─────────

    [Fact]
    public void 空响应体_抛出可读的中文异常()
    {
        var ex = Assert.Throws<DeepSeekEmptyResponseException>(() =>
            DeepSeekResponseParser.ParseWithMetadata(string.Empty, RequestItems()));

        Assert.Contains("空响应体", ex.Message);
    }

    [Fact]
    public void 空content_抛出可读的中文异常()
    {
        const string envelope = """{"id":"r1","choices":[{"message":{"content":""}}],"usage":{"prompt_tokens":1,"completion_tokens":0,"total_tokens":1}}""";

        var ex = Assert.Throws<DeepSeekEmptyResponseException>(() =>
            DeepSeekResponseParser.ParseWithMetadata(envelope, RequestItems()));

        Assert.Contains("content 为空", ex.Message);
        Assert.Contains("思考", ex.Message);
    }

    // ───────── ② 关思考降级重试：第一次失败、第二次成功 ─────────

    [Fact]
    public async Task 批失败后_关闭思考重试一次并成功()
    {
        var client = new FlakyBatchClient(failuresBeforeSuccess: 1);
        var provider = CreateProvider(client);
        var entries = CreateEntries();

        var results = await provider.TranslateAsync(entries, default, "Items.json", null);

        Assert.Equal(2, client.CallCount);                 // 1 次正常 + 1 次降级重试
        Assert.True(client.ThinkingEnabled[0]);            // 首次：思考开启
        Assert.False(client.ThinkingEnabled[1]);           // 降级：关闭思考
        Assert.Equal(2, results.Count);
        Assert.All(results.Values, result => Assert.False(result.NeedsReview));
        // 第9.0C.17轮：降级重试成功 ⇒ 不留下失败标记（否则会被误判为"需要重译"）
        Assert.All(entries, entry =>
        {
            Assert.False(entry.NeedsReview);
            Assert.False(entry.ProviderBatchFailed);
        });
    }

    // ───────── ③ 两次都失败 ⇒ 批级隔离（不抛异常 + 标记待审） ─────────

    [Fact]
    public async Task 批彻底失败_标记待审且不再抛出()
    {
        var client = new FlakyBatchClient(failuresBeforeSuccess: int.MaxValue);
        var logs = new List<string>();
        var provider = CreateProvider(client, logs.Add);
        var entries = CreateEntries();

        var results = await provider.TranslateAsync(entries, default, "Items.json", null);

        Assert.Empty(results);                             // 没有可信译文 ⇒ 不产生 TM / 缓存候选
        Assert.All(entries, entry =>
        {
            Assert.True(entry.NeedsReview);
            Assert.Contains("本批翻译失败", entry.ReviewReason);
            // 第9.0C.17轮：结构化标记（GUI 的「重译翻译失败的条目」与筛选项都读它，不解析上面的文案）
            Assert.True(entry.ProviderBatchFailed);
        });
        Assert.Contains(logs, line => line.Contains("已标记 2 条待人工审核"));
        Assert.Equal(2, client.CallCount);                 // 降级重试也失败，但没有继续抛异常
    }

    /// <summary>测试用批量客户端：前 N 次调用抛"空响应"异常（模拟真实故障），之后返回正常结果。</summary>
    private sealed class FlakyBatchClient : IDeepSeekBatchClient
    {
        private readonly int _failuresBeforeSuccess;

        public FlakyBatchClient(int failuresBeforeSuccess) => _failuresBeforeSuccess = failuresBeforeSuccess;

        public int CallCount { get; private set; }

        public List<bool> ThinkingEnabled { get; } = new();

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
            => TranslateBatchWithMetadataAsync(
                batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt,
                new DeepSeekRequestThinking(false, null), false, null, TranslationMode.EnglishOnly);

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt,
            DeepSeekRequestThinking thinking,
            bool includeModifiedRule,
            TextCategory? category,
            TranslationMode translationMode)
        {
            CallCount++;
            ThinkingEnabled.Add(thinking.Enabled);

            if (CallCount <= _failuresBeforeSuccess)
            {
                throw new DeepSeekEmptyResponseException("[错误] API 响应 content 为空（测试模拟）");
            }

            return Task.FromResult(new DeepSeekBatchResult
            {
                Items = items.ToDictionary(
                    item => item.Id,
                    item => new DeepSeekTranslateItem(item.Id, "测试译文", false, string.Empty),
                    StringComparer.Ordinal),
                ResponseId = "test",
                ResponseModel = "m",
                PromptTokens = 1,
                CompletionTokens = 1,
                TotalTokens = 2,
                RetryCount = 0,
                DurationMs = 1,
            });
        }
    }
}
