using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P2轮：Agent 级 E2E —— 四模式动作与 Provider 调用次数（验收 §12–§18）。
///
/// 走真实链：候选 DiffEntry → ApplyToEntries → 生产动作过滤 → Coordinator → TranslationAgent
/// → SqliteTranslationMemory（临时）→ Fake Provider。
/// 真实 DeepSeek 调用数恒为 0（Fake Provider 只回放，不发网络请求）。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class FourModeAgentActionE2ETests
{
    // A/B 场景：Old KR = A｜New KR = B｜EN 与 JP 不变｜旧中文存在
    private const string KoreanA = "안녕하세요";
    private const string KoreanB = "반갑습니다";
    private const string EnglishC = "Hello";
    private const string JapaneseC = "こんにちは";

    private static E2ESources Baseline() => new(new[] { KoreanA }, new[] { EnglishC }, new[] { JapaneseC });

    private static E2ESources KoreanChanged() => new(new[] { KoreanB }, new[] { EnglishC }, new[] { JapaneseC });

    private static E2ESources ReferenceOnlyEnglishChanged()
        => new(new[] { KoreanA }, new[] { "Hello changed" }, new[] { JapaneseC });

    private static E2ESources ReferenceOnlyJapaneseChanged()
        => new(new[] { KoreanA }, new[] { EnglishC }, new[] { "こんばんは" });

    // ───────────────────────── §12 EN_ONLY A/B ─────────────────────────

    [Fact]
    public async Task ENONLY_KR变化EN不变_动作继承且Provider调用为0()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), KoreanChanged());

        // 韩文确实变了（Canonical Diff 能看到），但 EN_ONLY 不受其影响
        Assert.Equal(1, capture.CanonicalDiff!.Modified);

        var provider = new FakeE2EProvider { Mode = TranslationMode.EnglishOnly };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider,
            oldEnglish: new[] { EnglishC },
            newEnglish: new[] { EnglishC });

        Assert.Equal(1, run.Inherit);
        Assert.Equal(0, run.TranslateNew);
        Assert.Equal(0, run.TranslateModified);
        Assert.Equal(0, run.TranslateMissing);
        Assert.Empty(run.AgentEntries);          // EN 未变 + 旧中文存在 → 不进 Agent
        Assert.Equal(0, provider.CallCount);     // Provider 调用数必须为 0

        // EN_ONLY 保持旧英文兼容：绝不注入任何 Canonical 字段 / Mode Salt
        var entry = Assert.Single(run.Candidates);
        Assert.Equal(EnglishC, entry.NewSourceText);
        Assert.Null(entry.CanonicalKoreanText);
        Assert.Null(entry.OldCanonicalKoreanText);
        Assert.Null(entry.SourceHashSalt);
    }

    // ───────────────────────── §13 KR_EN A/B ─────────────────────────

    [Fact]
    public async Task KREN_KR变化EN不变_动作修改且Provider调用为1并收到Canonical与盐()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), KoreanChanged());

        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, run.TranslateModified);
        Assert.Equal(0, run.Inherit);
        Assert.Equal(0, run.TranslateNew);
        Assert.Single(run.AgentEntries);
        Assert.Equal(1, provider.CallCount);     // KR 变化 → 必须调用 Provider（恰好 1 次）

        var call = Assert.Single(provider.Calls);
        Assert.Equal(TranslationMode.KoreanEnglish, call.Mode);
        var received = Assert.Single(call.Entries);
        Assert.Equal(TranslationAction.TranslateModified, received.Action);
        Assert.Equal("Items.json|1|dataList[0].name", received.UnitKey);
        Assert.Equal(KoreanB, received.CanonicalKorean);          // 当前韩文
        Assert.Equal(KoreanA, received.OldCanonicalKorean);       // 旧韩文
        Assert.NotNull(received.SourceHashSalt);                  // KR 模式盐非空
        Assert.Equal(EnglishC, received.SourceText);              // 翻译依据 = 英文参考
    }

    // ───────────────────────── §14 KR_JP / KR_ONLY ─────────────────────────

    [Fact]
    public async Task KRJP_KR变化_动作修改且Provider调用为1且依据为日文()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), KoreanChanged());

        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanJapanese };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanJapanese,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, run.TranslateModified);
        Assert.Equal(1, provider.CallCount);

        var received = Assert.Single(Assert.Single(provider.Calls).Entries);
        Assert.Equal(JapaneseC, received.SourceText);              // 翻译依据 = 日文参考
        Assert.Equal(KoreanB, received.CanonicalKorean);
        Assert.Equal(KoreanA, received.OldCanonicalKorean);
        Assert.NotNull(received.SourceHashSalt);
    }

    [Fact]
    public async Task KRONLY_KR变化_动作修改且Provider调用为1且依据为韩文()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), KoreanChanged());

        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanOnly };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanOnly,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, run.TranslateModified);
        Assert.Equal(1, provider.CallCount);

        var received = Assert.Single(Assert.Single(provider.Calls).Entries);
        Assert.Equal(KoreanB, received.SourceText);                // 纯韩文模式：依据就是韩文
        Assert.Equal(KoreanB, received.CanonicalKorean);
        Assert.Equal(KoreanA, received.OldCanonicalKorean);
        Assert.NotNull(received.SourceHashSalt);
    }

    // ─────────────────── §15 Reference-only：只有英文变化（KR 未变） ───────────────────

    [Fact]
    public async Task KREN_仅英文参考变化_动作保持继承且Provider调用为0()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), ReferenceOnlyEnglishChanged());

        Assert.Equal(0, capture.CanonicalDiff!.Modified);
        Assert.Equal(1, capture.CanonicalDiff.EnglishChangedCount);
        Assert.Contains(capture.CanonicalDiff.EnglishChanges,
            change => change.Kind == LimbusTranslator.Infrastructure.Multilingual.ReferenceChangeKind.Changed);

        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, run.Inherit);            // 韩文未变 + 旧中文存在 → 继承
        Assert.Equal(0, run.TranslateModified);
        Assert.Equal(0, run.TranslateNew);
        Assert.Empty(run.AgentEntries);
        Assert.Equal(0, provider.CallCount);     // 参考译本变化不得触发重翻

        // 英文参考变化仍被记录（诊断可见）
        Assert.Equal("Hello changed", run.Capture!.Sources[FourModeAgentE2EHarness.KeyOf(0)].English);
    }

    // ─────────────────── §16 Reference-only：只有日文变化（KR 未变） ───────────────────

    [Fact]
    public async Task KRJP_仅日文参考变化_动作保持继承且Provider调用为0()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), ReferenceOnlyJapaneseChanged());

        Assert.Equal(0, capture.CanonicalDiff!.Modified);
        Assert.Equal(1, capture.CanonicalDiff.JapaneseChangedCount);

        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanJapanese };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanJapanese,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, run.Inherit);
        Assert.Equal(0, run.TranslateModified);
        Assert.Empty(run.AgentEntries);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal("こんばんは", run.Capture!.Sources[FourModeAgentE2EHarness.KeyOf(0)].Japanese);
    }

    // ─────────────────── §17 Baseline 迁移：100 条全部继承 ───────────────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task 首次基线100条_全部继承且Provider调用为0(TranslationMode mode)
    {
        using var harness = new FourModeAgentE2EHarness();

        // 无 Previous KR：只写入当前三语树并捕获一次（首次建立 Canonical Baseline）
        harness.WriteAll(new E2ESources(
            FourModeAgentE2EHarness.Texts(100, "원문"),
            FourModeAgentE2EHarness.Texts(100, "Source "),
            FourModeAgentE2EHarness.Texts(100, "原文")));
        var capture = harness.Capture();

        Assert.True(capture.IsFirstCanonicalBaseline);

        var provider = new FakeE2EProvider { Mode = mode };
        var run = await harness.RunProductionChainAsync(
            mode,
            capture,
            FourModeAgentE2EHarness.OldChinese(100),
            _ => provider);

        Assert.Equal(100, run.Inherit);
        Assert.Equal(0, run.TranslateNew);
        Assert.Equal(0, run.TranslateModified);
        Assert.Equal(0, run.TranslateMissing);
        Assert.Empty(run.AgentEntries);

        // 关键：不是只断言 Action，而是断言 Provider 真实调用次数
        Assert.Equal(0, provider.CallCount);
    }

    // ─────────────────── §18 Baseline 缺旧中文：TranslateMissing ───────────────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task 首次基线缺旧中文_动作缺译且Provider调用为1(TranslationMode mode)
    {
        using var harness = new FourModeAgentE2EHarness();

        harness.WriteAll(new E2ESources(
            FourModeAgentE2EHarness.Texts(3, "새원문"),
            FourModeAgentE2EHarness.Texts(3, "New source "),
            FourModeAgentE2EHarness.Texts(3, "新原文")));
        var capture = harness.Capture();

        Assert.True(capture.IsFirstCanonicalBaseline);

        var provider = new FakeE2EProvider { Mode = mode };
        var run = await harness.RunProductionChainAsync(
            mode,
            capture,
            oldChinese: null,
            providerFactory: _ => provider);

        Assert.Equal(0, run.Inherit);
        Assert.Equal(3, run.TranslateMissing);
        Assert.Equal(0, run.TranslateNew);
        Assert.Equal(0, run.TranslateModified);
        Assert.Equal(3, run.AgentEntries.Count);
        Assert.Equal(1, provider.CallCount);       // 一次 Provider 调用覆盖本 Stage 全部缺译条目
        Assert.Equal(3, provider.ReceivedEntries.Count);
    }

    // ─────────────────── 统一生产顺序回归（P3：P1-1 / P1-2 已修） ───────────────────

    /// <summary>
    /// P1-1 修复回归：「KR 变、EN 不变、旧中文存在」的条目必须**被枚举并进入 Agent**。
    /// （修复前：候选集只来自 EN Diff ⇒ 被判 Inherit，韩文变化被静默忽略，ProviderCalls = 0。）
    /// </summary>
    [Fact]
    public async Task 统一生产顺序_KR变化EN不变仍进入Agent()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), KoreanChanged());

        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, capture.CanonicalDiff!.Modified);    // 韩文确实变了
        Assert.Equal(1, run.TranslateModified);              // 接线把 EN 的 Inherit 改写为 TranslateModified
        Assert.Single(run.AgentEntries);                     // ⇒ 进入 Agent
        Assert.Equal(1, provider.CallCount);                 // ⇒ Provider 调用 1 次
        Assert.Equal(1, run.PatchedCount);
    }

    /// <summary>
    /// P1-2 修复回归：接线把 TranslateModified 降级为 Inherit 的条目必须**不再进入 Agent**，
    /// 且旧中文必须保持（用旧中文物化译文，绝不被 AI 译文覆盖）。
    /// </summary>
    [Fact]
    public async Task 统一生产顺序_接线后降级为继承则不调用Provider且旧中文保持()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), ReferenceOnlyEnglishChanged());

        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider,
            // 英文参考确实变了 ⇒ EN Diff 判定 TranslateModified（Translation 为空），
            // 随后被 Canonical（KR 未变 + 旧中文存在）降级为 Inherit。
            oldEnglish: new[] { EnglishC },
            newEnglish: new[] { "Hello changed" });

        var entry = Assert.Single(run.Candidates);
        Assert.Equal(TranslationAction.Inherit, entry.Action);   // 接线后动作 = Inherit
        Assert.Empty(run.AgentEntries);                          // 不再进入 Agent
        Assert.Equal(0, provider.CallCount);                     // 不消耗 Provider 调用
        Assert.Equal(1, run.InheritedKeptCount);                 // 接线后转继承计数
        Assert.Equal("旧中文1", entry.Translation);              // 旧中文被保持（不是 AI 译文）
        Assert.Equal(TranslationSource.Inherited, entry.Provenance);
    }
}
