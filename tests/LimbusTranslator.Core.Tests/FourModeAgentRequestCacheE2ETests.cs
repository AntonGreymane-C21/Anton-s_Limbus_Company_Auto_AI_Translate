using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P2轮：Agent 级 E2E —— request_cache 实链（验收 §22–§25）。
///
/// 真实链路：Coordinator → TranslationAgent → DeepSeekTranslationProvider（真实指纹 v3 + 真实
/// request_cache 读写）→ FakeE2EBatchClient（BatchCallCount 即真实网络调用次数，恒为 0 才算安全）。
/// TM 与 Cache 使用两个独立的临时 SQLite；两次运行之间清空 translations，
/// 以确保第二次命中的确实是 request_cache 而不是 Translation Memory。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class FourModeAgentRequestCacheE2ETests
{
    private const string KoreanA = "안녕하세요";
    private const string KoreanB = "반갑습니다";
    private const string EnglishOld = "Hello there my friend";
    private const string EnglishNew = "Hello there my dear friend";
    private const string JapaneseC = "こんにちは世界";

    /// <summary>较长的中文假译文：避免长度比例校验产生噪音。</summary>
    private const string FakeTranslation = "这是一条用于端到端测试的中文译文";

    /// <summary>四模式都"有变化"的场景：KR 变 + EN 变（JP 不变）⇒ 四种模式都有候选条目。</summary>
    private static E2ESources Baseline() => new(new[] { KoreanA }, new[] { EnglishOld }, new[] { JapaneseC });

    private static E2ESources Current() => new(new[] { KoreanB }, new[] { EnglishNew }, new[] { JapaneseC });

    private static FakeE2EBatchClient NewClient()
        => new() { TranslationFactory = _ => FakeTranslation };

    private static Func<TranslationCacheServices, ITranslationProvider> DeepSeekFactory(
        TranslationMode mode,
        FakeE2EBatchClient client,
        Action<string>? log = null)
        => services => new DeepSeekTranslationProvider(
            new DeepSeekOptions
            {
                ApiKey = "sk-e2e-fake",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "e2e-test-model",
                MaxRetry = 0,
            },
            configDir: null,
            batchOptions: null,
            cacheServices: services,
            client: client,
            log: log,
            translationMode: mode);

    // ───────────── §22 同模式第二次：真实 Cache 命中 ─────────────

    [Fact]
    public async Task 同模式第二次_命中RequestCache且Batch调用为0()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), Current());

        // 第一次：Cache Miss → 真实写入一条 request_cache
        var firstClient = NewClient();
        var first = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            DeepSeekFactory(TranslationMode.KoreanEnglish, firstClient));

        Assert.Equal(1, first.TranslateModified);
        Assert.Equal(1, firstClient.BatchCallCount);
        Assert.Equal(1, firstClient.TotalItemCount);
        Assert.Equal(TranslationMode.KoreanEnglish, Assert.Single(firstClient.Modes));   // 模式真实贯通到请求
        Assert.Equal(1, harness.RequestCacheRowCount());        // CacheWrite = 1
        Assert.Equal(1, first.TraceNetworkCalls);
        Assert.Equal(0, first.TraceCacheHits);

        // 清空 TM：第二次必须靠 request_cache 而不是 Translation Memory
        harness.ClearTranslations();

        // 第二次：完全相同请求 → Cache Hit（Provider 不再访问网络）
        var secondClient = NewClient();
        var second = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            DeepSeekFactory(TranslationMode.KoreanEnglish, secondClient));

        Assert.Equal(0, secondClient.BatchCallCount);           // 真实 Cache 命中：网络调用 = 0
        Assert.Equal(1, harness.RequestCacheRowCount());        // 没有新增缓存写入
        Assert.Equal(1, second.TraceCacheHits);
        Assert.Equal(0, second.TraceNetworkCalls);

        // 两次请求指纹必须完全一致（否则说明指纹引入了运行时噪声）
        Assert.Equal(first.TraceFingerprints, second.TraceFingerprints);
        Assert.Single(second.TraceFingerprints);
    }

    // ───────────── §23 跨模式：不得命中别人的 Cache ─────────────

    [Theory]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    [InlineData(TranslationMode.EnglishOnly)]
    public async Task 跨模式_不得命中其它模式的Cache(TranslationMode secondMode)
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), Current());

        // 先用 KR_EN 生成缓存
        var krClient = NewClient();
        var krRun = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            DeepSeekFactory(TranslationMode.KoreanEnglish, krClient));

        Assert.Equal(1, krClient.BatchCallCount);
        Assert.Equal(1, harness.RequestCacheRowCount());
        var krFingerprints = krRun.TraceFingerprints;

        // 清空 TM，保证第二次是否命中取决于 request_cache
        harness.ClearTranslations();

        var otherClient = NewClient();
        var otherRun = await harness.RunProductionChainAsync(
            secondMode,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            DeepSeekFactory(secondMode, otherClient),
            oldEnglish: new[] { EnglishOld },
            newEnglish: new[] { EnglishNew });

        Assert.Single(otherRun.AgentEntries);
        Assert.Equal(1, otherClient.BatchCallCount);          // 跨模式必须 Cache Miss → 真实调用
        Assert.Equal(0, otherRun.TraceCacheHits);
        Assert.Equal(1, otherRun.TraceNetworkCalls);
        Assert.Equal(2, harness.RequestCacheRowCount());      // 新指纹写入了第二条缓存

        // 两种模式的指纹必须互不相同
        Assert.DoesNotContain(otherRun.TraceFingerprints, fingerprint => krFingerprints.Contains(fingerprint));
    }

    // ───────────── §24 Fallback：三个模式文本相同，但 Cache 不得互相命中 ─────────────

    [Fact]
    public async Task Fallback三模式_文本相同也不得跨模式命中Cache()
    {
        using var harness = new FourModeAgentE2EHarness();

        // KR 变化 + EN / JP 缺失 → 三个 KR 模式最终 Selected Source 都是同一份韩文
        var capture = harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, null, null),
            new E2ESources(new[] { KoreanB }, null, null));

        var oldChinese = FourModeAgentE2EHarness.OldChinese(1);

        // ① KR_EN 第一次：Cache Miss → 写入缓存
        var firstClient = NewClient();
        var first = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish, capture, oldChinese,
            DeepSeekFactory(TranslationMode.KoreanEnglish, firstClient, harness.Logs.Add));

        Assert.Equal(1, firstClient.BatchCallCount);
        Assert.Equal(1, harness.RequestCacheRowCount());
        Assert.Equal(KoreanB, firstClient.Sources.Single());     // 回退韩文

        // ② KR_EN 第二次：同模式 → 必须命中
        harness.ClearTranslations();
        var repeatClient = NewClient();
        var repeat = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish, capture, oldChinese,
            DeepSeekFactory(TranslationMode.KoreanEnglish, repeatClient, harness.Logs.Add));

        Assert.Equal(0, repeatClient.BatchCallCount);
        Assert.Equal(1, repeat.TraceCacheHits);
        Assert.Equal(1, harness.RequestCacheRowCount());

        // ③ KR_JP / KR_ONLY：最终文本完全相同，但模式不同 → 不得命中
        foreach (var mode in new[] { TranslationMode.KoreanJapanese, TranslationMode.KoreanOnly })
        {
            harness.ClearTranslations();
            var client = NewClient();
            var run = await harness.RunProductionChainAsync(
                mode, capture, oldChinese, DeepSeekFactory(mode, client, harness.Logs.Add));

            Assert.Equal(1, client.BatchCallCount);               // 但必须 Cache Miss
            Assert.Equal(0, run.TraceCacheHits);
            Assert.Equal(KoreanB, client.Sources.Single());       // 最终文本完全相同
            Assert.Equal(mode, client.Modes.Single());            // 请求必须携带本 Run 锁定的模式
        }

        // 三种模式各留下一条自己的缓存
        Assert.Equal(3, harness.RequestCacheRowCount());
    }

    // ───────────── §25 EN_ONLY：Cache 不受韩文变化影响 ─────────────

    [Fact]
    public async Task ENONLY的Cache_不受韩文变化影响()
    {
        using var harness = new FourModeAgentE2EHarness();

        // EN 未变、旧中文缺失 → TranslateMissing；此时 KR = A
        harness.WriteAll(new E2ESources(new[] { KoreanA }, new[] { EnglishOld }, null));
        var captureBefore = harness.Capture();

        var firstClient = NewClient();
        var first = await harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            captureBefore,
            oldChinese: null,
            DeepSeekFactory(TranslationMode.EnglishOnly, firstClient),
            oldEnglish: new[] { EnglishOld },
            newEnglish: new[] { EnglishOld });

        Assert.Equal(1, firstClient.BatchCallCount);
        Assert.Equal(1, harness.RequestCacheRowCount());
        Assert.Equal(EnglishOld, firstClient.Sources.Single());
        Assert.All(firstClient.CanonicalKoreans, canonical => Assert.Null(canonical));   // 请求体不得出现韩文
        Assert.Equal(1, first.TraceNetworkCalls);

        // 韩文发生变化（英文完全不变）
        harness.WriteAll(new E2ESources(new[] { KoreanB }, new[] { EnglishOld }, null));
        var captureAfter = harness.Capture();
        Assert.Equal(1, captureAfter.CanonicalDiff!.Modified);

        harness.ClearTranslations();

        var secondClient = NewClient();
        var second = await harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            captureAfter,
            oldChinese: null,
            DeepSeekFactory(TranslationMode.EnglishOnly, secondClient),
            oldEnglish: new[] { EnglishOld },
            newEnglish: new[] { EnglishOld });

        Assert.Equal(0, secondClient.BatchCallCount);          // 韩文变化不得使 EN_ONLY 缓存失效
        Assert.Equal(1, second.TraceCacheHits);
        Assert.Equal(1, harness.RequestCacheRowCount());
        Assert.Equal(first.TraceFingerprints, second.TraceFingerprints);
    }
}
