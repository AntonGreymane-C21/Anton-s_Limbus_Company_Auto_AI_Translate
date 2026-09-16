using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P2轮：Agent 级 E2E —— TM 实链（验收 §19–§21）。
///
/// 全部使用临时 SQLite（tm.db）/ 临时 request_cache / Fake Provider：
/// 真实 DeepSeek 调用数恒为 0，生产 translation_memory.db 绝不触碰。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class FourModeAgentTmE2ETests
{
    private const string KoreanA = "안녕하세요";
    private const string KoreanB = "반갑습니다";
    private const string EnglishC = "Hello there";
    private const string JapaneseC = "こんにちは世界";

    // ───────────── §19 EN_ONLY：旧格式（无盐）TM 的 Agent 级 Exact Hit ─────────────

    [Fact]
    public async Task ENONLY_旧无盐TM_Agent级ExactHit且Provider调用为0()
    {
        using var harness = new FourModeAgentE2EHarness();

        // EN 未变 + 旧中文缺失 → TranslateMissing（真正会进入 Agent 的条目）
        harness.WriteAll(new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null));
        var capture = harness.Capture();

        // 插入「历史旧格式」记录：SourceHash = 历史无盐算法
        var key = FourModeAgentE2EHarness.UnitKeyOf(0);
        harness.InsertLegacyTranslationRecord(key, EnglishC, "旧版无盐译文");

        // 历史无盐哈希必须与 salt = null 的哈希逐字节相同（旧 TM 兼容的底层前提）
        Assert.Equal(
            SqliteTranslationMemory.ComputeSourceHash(EnglishC),
            SqliteTranslationMemory.ComputeSourceHash(EnglishC, null));

        var provider = new FakeE2EProvider { Mode = TranslationMode.EnglishOnly };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            oldChinese: null,
            providerFactory: _ => provider,
            oldEnglish: new[] { EnglishC },
            newEnglish: new[] { EnglishC });

        var entry = Assert.Single(run.AgentEntries);
        Assert.Null(entry.SourceHashSalt);                                            // EN_ONLY：盐必须为 null
        Assert.Equal(TranslationMemoryMatchType.ExactUnit, entry.TmMatchType);        // Agent 级 Exact TM 命中
        Assert.Equal("旧版无盐译文", entry.Translation);
        Assert.Equal(TranslationSource.AI, entry.Provenance);                         // 记录来源被传播
        Assert.Equal(0, provider.CallCount);                                          // 命中 → 不调用 Provider
        Assert.Equal(1, run.Coordinator.TotalTmHits);                                 // Agent 汇总也是命中
    }

    // ───────────── §20 三个 KR 模式：不得命中旧（无盐）EN TM ─────────────

    /// <summary>
    /// 数据库中只有一条旧 EN TM 记录，且 KR 模式条目的「可见 Source」与它完全相同；
    /// KR 模式因为 SourceHashSalt != null 必须 Miss（拒绝跨模式复用）。
    /// </summary>
    [Theory]
    [InlineData(TranslationMode.KoreanEnglish, EnglishC)]
    [InlineData(TranslationMode.KoreanJapanese, JapaneseC)]
    [InlineData(TranslationMode.KoreanOnly, KoreanB)]
    public async Task KR模式_可见源文相同也不得命中旧无盐EN_TM(TranslationMode mode, string visibleSource)
    {
        using var harness = new FourModeAgentE2EHarness();

        var english = mode == TranslationMode.KoreanJapanese ? null : new[] { EnglishC };
        var japanese = mode == TranslationMode.KoreanEnglish ? null : new[] { JapaneseC };

        harness.WriteAll(new E2ESources(new[] { KoreanA }, english, japanese));
        harness.Capture();                                   // 建立 Previous KR = A
        harness.WriteAll(new E2ESources(new[] { KoreanB }, english, japanese));
        var capture = harness.Capture();                     // 当前 KR = B（Modified）

        var key = FourModeAgentE2EHarness.UnitKeyOf(0);
        harness.InsertLegacyTranslationRecord(key, visibleSource, "旧版英文译文");

        var provider = new FakeE2EProvider { Mode = mode };
        var run = await harness.RunProductionChainAsync(
            mode,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            providerFactory: _ => provider);

        Assert.Equal(1, run.TranslateModified);
        Assert.Equal(1, provider.CallCount);

        var received = Assert.Single(Assert.Single(provider.Calls).Entries);
        Assert.Equal(visibleSource, received.SourceText);            // 可见源文与旧记录完全一致
        Assert.NotNull(received.SourceHashSalt);                     // 但 KR 模式带盐

        var entry = Assert.Single(run.AgentEntries);
        Assert.NotEqual(TranslationMemoryMatchType.ExactUnit, entry.TmMatchType);
        Assert.Equal(0, run.Coordinator.TotalTmHits);
    }

    // ───────────── §21 Fallback：TM 盐隔离（实链） ─────────────

    [Fact]
    public async Task Fallback三模式_盐两两不同且TM不跨模式命中()
    {
        using var harness = new FourModeAgentE2EHarness();

        // KR 存在、EN 与 JP 缺失 → 三个 KR 模式最终 Selected Source 都是同一份韩文
        var capture = harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, null, null),
            new E2ESources(new[] { KoreanB }, null, null));

        // ① 盐三者两两不同（即使最终文本完全相同）
        var salts = new[]
        {
            TranslationModePolicy.BuildModeSalt(TranslationMode.KoreanEnglish, SourceLanguage.Korean, KoreanB),
            TranslationModePolicy.BuildModeSalt(TranslationMode.KoreanJapanese, SourceLanguage.Korean, KoreanB),
            TranslationModePolicy.BuildModeSalt(TranslationMode.KoreanOnly, SourceLanguage.Korean, KoreanB),
        };
        Assert.All(salts, salt => Assert.NotNull(salt));
        Assert.Equal(3, salts.Distinct(StringComparer.Ordinal).Count());

        // ② SourceHash 三者两两不同
        var hashes = salts
            .Select(salt => SqliteTranslationMemory.ComputeSourceHash(KoreanB, salt))
            .ToList();
        Assert.Equal(3, hashes.Distinct(StringComparer.Ordinal).Count());

        // ③ 实链：三种模式各自调用 Provider（互不命中），并把记录写进同一张临时 TM
        foreach (var mode in new[]
        {
            TranslationMode.KoreanEnglish,
            TranslationMode.KoreanJapanese,
            TranslationMode.KoreanOnly,
        })
        {
            var provider = new FakeE2EProvider { Mode = mode };
            var run = await harness.RunProductionChainAsync(
                mode,
                capture,
                FourModeAgentE2EHarness.OldChinese(1),
                providerFactory: _ => provider);

            Assert.Equal(1, run.TranslateModified);
            Assert.Equal(1, provider.CallCount);

            var received = Assert.Single(Assert.Single(provider.Calls).Entries);
            Assert.Equal(KoreanB, received.SourceText);          // 三种模式最终文本完全相同
            Assert.NotNull(received.SourceHashSalt);
            Assert.Equal(0, run.Coordinator.TotalTmHits);
        }

        // 三次运行各写入 1 条 ⇒ 没有任何一次命中别人写下的记录
        Assert.Equal(3, harness.TranslationRowCount());
    }

    /// <summary>
    /// 同模式、同数据的第二次运行必须命中自己写下的 TM（ExactUnit），Provider 调用为 0。
    /// 【回归证据】该用例直接暴露「TM 落库未带 Mode Salt」的真实问题：写入用无盐哈希、
    /// 查询用盐哈希 ⇒ KR 模式的 TM 永远无法命中（每次运行都要重新调用 Provider）。
    /// </summary>
    [Fact]
    public async Task Fallback同模式_第二次应命中TM且Provider调用为0()
    {
        using var harness = new FourModeAgentE2EHarness();

        var capture = harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, null, null),
            new E2ESources(new[] { KoreanB }, null, null));

        var firstProvider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish };
        var first = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            providerFactory: _ => firstProvider);

        Assert.Equal(1, firstProvider.CallCount);
        Assert.Equal(1, harness.TranslationRowCount());

        var secondProvider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish };
        var second = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            providerFactory: _ => secondProvider);

        var entry = Assert.Single(second.AgentEntries);
        Assert.Equal(TranslationMemoryMatchType.ExactUnit, entry.TmMatchType);
        Assert.Equal(0, secondProvider.CallCount);
        Assert.Equal(1, second.Coordinator.TotalTmHits);
    }

    /// <summary>
    /// KR_EN 写入的 TM 记录不得被 EN_ONLY 命中（可见 SourceText 完全相同）。
    /// 【回归证据】该用例暴露「TM 落库未带 Mode Salt」造成的跨模式污染：
    /// KR 模式写入的**无盐**记录会被 EN_ONLY 命中，使汉化结果串模式。
    /// </summary>
    [Fact]
    public async Task KREN写入的TM_不得被ENONLY命中()
    {
        using var harness = new FourModeAgentE2EHarness();

        var capture = harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, new[] { EnglishC }, null),
            new E2ESources(new[] { KoreanB }, new[] { EnglishC }, null));

        // 第一次：KR_EN（韩文变化 → TranslateModified；翻译依据 = 英文原文）
        var krProvider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish };
        var krRun = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            oldChinese: null,
            providerFactory: _ => krProvider);
        Assert.Equal(1, krRun.TranslateModified);
        Assert.Equal(1, krProvider.CallCount);
        Assert.Equal(1, harness.TranslationRowCount());

        // 第二次：EN_ONLY（英文未变、旧中文缺失 → TranslateMissing），可见源文与上面完全一致
        var enProvider = new FakeE2EProvider { Mode = TranslationMode.EnglishOnly };
        var enRun = await harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            oldChinese: null,
            providerFactory: _ => enProvider,
            oldEnglish: new[] { EnglishC },
            newEnglish: new[] { EnglishC });

        var entry = Assert.Single(enRun.AgentEntries);
        Assert.Equal(EnglishC, entry.NewSourceText);
        Assert.Null(entry.SourceHashSalt);
        Assert.NotEqual(TranslationMemoryMatchType.ExactUnit, entry.TmMatchType);  // 不得命中 KR 模式写入的记录
        Assert.Equal(1, enProvider.CallCount);
    }
}
