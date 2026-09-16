using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;
using LimbusTranslator.Infrastructure.Translation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P5轮：KR 权威模式的**旧中文继承对齐**验收（修复 P4 报告 P1-α）。
///
/// 核心语义：KR 权威模式下旧中文一律**以 UnitKey 为准** ——
/// 即使某个 Key 只存在于韩文树（英文 Diff 里没有），只要旧中文有同一个 UnitKey，就必须 Inherit；
/// 旧中文没有该 UnitKey 时仍必须 TranslateMissing；Modified / Deleted / Baseline 规则不变。
///
/// 全部走真实链路（三语捕获 → 生产计划 → Fake Provider / Fake Batch Client → 真实 Merge → 真实 Gate → 解析真实 output JSON），
/// 一切落盘都在临时目录；真实 API 调用数恒为 0。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class KrOldChineseInheritanceTests : IDisposable
{
    private const string KoreanA = "안녕하세요";
    private const string KoreanB = "반갑습니다";
    private const string EnglishC = "Hello";
    private const string JapaneseC = "こんにちは";
    private const string FakeTranslation = "测试译文";

    private readonly FourModeAgentE2EHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>KR-only 的 Key（第 4 条记录；英文树里没有它）。</summary>
    private static string KoreanOnlyKey => FourModeAgentE2EHarness.KeyOf(3);

    private static readonly TranslationMode[] KoreanModes =
    {
        TranslationMode.KoreanEnglish,
        TranslationMode.KoreanJapanese,
        TranslationMode.KoreanOnly,
    };

    private sealed record Run(
        ProductionTranslationPlan Plan,
        FakeE2EProvider Provider,
        OutputMergeResult Merge,
        ReleaseGateResult Gate,
        string OutputRoot);

    /// <summary>跑真实链路：capture → 计划 → Agent → Merge → Gate。</summary>
    private async Task<Run> RunAsync(
        TranslationMode mode,
        E2ESources? baseline,
        E2ESources current,
        IReadOnlyDictionary<string, string>? oldChinese,
        bool firstCaptureOnly = false)
    {
        var capture = firstCaptureOnly
            ? WriteAndCapture(current)
            : _harness.CaptureAfterBaseline(baseline!, current);

        var provider = new FakeE2EProvider { Mode = mode, TranslationFactory = _ => FakeTranslation };
        var run = await _harness.RunProductionChainAsync(mode, capture, oldChinese, _ => provider);
        var plan = run.Plan;

        var outputRoot = Path.Combine(_harness.Root, "output");
        var merge = new MergeOutputService().MergeAllWithReport(
            plan.AuthoritativeDirectory,
            Coordinator.CollectTranslations(plan.OutputEntries),
            outputRoot,
            plan.ExpectedOutputKeys,
            plan.AuthoritativeLanguage);

        var gate = ReleaseGateService.Evaluate(
            plan.OutputEntries,
            null,
            null,
            new ReleaseGateKeySet
            {
                ExpectedKeys = plan.ExpectedOutputKeys,
                OutputKeys = merge.WrittenKeys,
                AuthoritativeSourceCode = SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage),
            });

        return new Run(plan, provider, merge, gate, outputRoot);
    }

    /// <summary>首次捕获（没有任何 Previous KR ⇒ Baseline 迁移模式）。</summary>
    private MultilingualCaptureResult WriteAndCapture(E2ESources current)
    {
        _harness.WriteAll(current);
        return _harness.Capture();
    }

    /// <summary>解析真实 output JSON 的 (id, name) 列表。</summary>
    private static List<(int Id, string? Name)> ReadOutput(Run run)
    {
        var path = Path.Combine(run.OutputRoot, FourModeAgentE2EHarness.LogicalFileName);
        Assert.True(File.Exists(path), $"output 文件不存在: {path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("dataList")
            .EnumerateArray()
            .Select(item => (item.GetProperty("id").GetInt32(), item.GetProperty("name").GetString()))
            .ToList();
    }

    // ───────── A / B / C：KR 有 D、EN 无 D、Old ZH 有 D、KR 未变 ⇒ Inherit ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task ABC_KR模式_KR独有Key有旧中文时必须继承(TranslationMode mode)
    {
        var run = await RunAsync(
            mode,
            baseline: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                new[] { JapaneseC, JapaneseC, JapaneseC }),
            current: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                new[] { JapaneseC, JapaneseC, JapaneseC }),
            oldChinese: FourModeAgentE2EHarness.OldChinese(4));

        Assert.True(run.Plan.IsKoreanAuthoritative);
        Assert.Equal(1, run.Plan.KoreanOnlyCount);                     // D 不在英文结构里
        Assert.Equal(4, run.Plan.InheritCount);
        Assert.Equal(0, run.Plan.TranslateMissingCount);               // 关键：不再退化成 TranslateMissing
        Assert.Equal(4, run.Plan.InheritedKeptCount);
        Assert.Empty(run.Plan.NeedTranslate);

        var inherited = run.Plan.OutputEntries.Single(entry => entry.Key.ToString() == KoreanOnlyKey);
        Assert.Equal(TranslationAction.Inherit, inherited.Action);
        Assert.Equal("旧中文4", inherited.Translation);                // Translation = D 的旧中文
        Assert.Equal("旧中文4", inherited.OldTranslation);
        Assert.Equal(TranslationSource.Inherited, inherited.Provenance);
        Assert.False(inherited.NeedsReview);

        Assert.Equal(0, run.Provider.CallCount);                       // 不进入 Agent
        Assert.Equal(4, run.Merge.WrittenEntryCount);                  // 继承结果真的写入 output
        Assert.Equal("旧中文1", ReadOutput(run)[0].Name);              // 同文件其它条目也正常写入

        var output = ReadOutput(run);
        Assert.Equal(4, output.Count);
        Assert.Equal("旧中文4", output[3].Name);
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
        Assert.NotEqual(ReleaseGateStatus.Blocked, run.Gate.Status);
    }

    // ───────── D：KR 有 D、EN 无 D、Old ZH **也没有** D ⇒ TranslateMissing 并进入 Agent ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task D_KR独有Key无旧中文时仍要缺译并进入Agent(TranslationMode mode)
    {
        var run = await RunAsync(
            mode,
            baseline: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                new[] { JapaneseC, JapaneseC, JapaneseC }),
            current: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                new[] { JapaneseC, JapaneseC, JapaneseC }),
            oldChinese: FourModeAgentE2EHarness.OldChinese(3));   // 只有前 3 条有旧中文

        Assert.Equal(3, run.Plan.InheritCount);
        Assert.Equal(1, run.Plan.TranslateMissingCount);

        var missing = run.Plan.OutputEntries.Single(entry => entry.Key.ToString() == KoreanOnlyKey);
        Assert.Equal(TranslationAction.TranslateMissing, missing.Action);
        Assert.Null(missing.OldTranslation);

        Assert.Single(run.Plan.NeedTranslate);
        Assert.Equal(1, run.Provider.CallCount);
        Assert.Equal(KoreanOnlyKey, Assert.Single(run.Provider.ReceivedEntries).UnitKey);

        var output = ReadOutput(run);
        Assert.Equal(4, output.Count);
        Assert.Equal(FakeTranslation, output[3].Name);                 // 第 4 条是新翻译结果
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
    }

    // ───────── E：Current KR 删除 D（Old ZH 与 EN 都还有 D）⇒ 不得继承、不得进 Agent、不得进 output ─────────

    [Fact]
    public async Task E_韩文已删除的Key即使有旧中文也不得继承或输出()
    {
        var run = await RunAsync(
            TranslationMode.KoreanEnglish,
            baseline: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanB },
                new[] { EnglishC, EnglishC, EnglishC, EnglishC },
                null),
            current: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC, EnglishC },
                null),
            oldChinese: FourModeAgentE2EHarness.OldChinese(4));

        var deleted = run.Plan.Candidates.Single(entry => entry.Key.ToString() == KoreanOnlyKey);
        Assert.Equal(TranslationAction.SkipDeleted, deleted.Action);
        Assert.Null(deleted.Translation);                              // 旧中文没有被物化成继承译文
        Assert.DoesNotContain(KoreanOnlyKey, run.Plan.ExpectedOutputKeys);
        Assert.DoesNotContain(run.Plan.OutputEntries, entry => entry.Key.ToString() == KoreanOnlyKey);
        Assert.Empty(run.Plan.NeedTranslate);
        Assert.Equal(0, run.Provider.CallCount);

        var output = ReadOutput(run);
        Assert.Equal(3, output.Count);
        Assert.DoesNotContain(output, item => item.Id == 4);
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
    }

    // ───────── F：KR-only Key 发生修改（Previous KR = A、Current KR = B、旧中文存在）⇒ Modified，不得错误继承 ─────────

    [Fact]
    public async Task F_KR独有Key发生修改时不得因为旧中文而错误继承()
    {
        var run = await RunAsync(
            TranslationMode.KoreanEnglish,
            baseline: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                null),
            current: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanB },   // D 的韩文变了
                new[] { EnglishC, EnglishC, EnglishC },
                null),
            oldChinese: FourModeAgentE2EHarness.OldChinese(4));

        var modified = run.Plan.Candidates.Single(entry => entry.Key.ToString() == KoreanOnlyKey);
        Assert.Equal(DiffKind.Modified, modified.DiffKind);
        Assert.Equal(TranslationAction.TranslateModified, modified.Action);   // 动作仍由 Canonical 计划决定
        Assert.Equal("旧中文4", modified.OldTranslation);                     // 旧中文只提供参考文本
        Assert.Equal(FakeTranslation, modified.Translation);                  // 结果是重译产物，而不是旧中文
        Assert.NotEqual(modified.OldTranslation, modified.Translation);
        Assert.Equal(TranslationSource.AI, modified.Provenance);

        Assert.Equal(3, run.Plan.InheritCount);
        Assert.Equal(1, run.Plan.TranslateModifiedCount);
        Assert.Equal(1, run.Provider.CallCount);
        var received = Assert.Single(run.Provider.ReceivedEntries);
        Assert.Equal(KoreanOnlyKey, received.UnitKey);
        Assert.Equal(KoreanB, received.CanonicalKorean);                      // 当前韩文
        Assert.Equal(KoreanA, received.OldCanonicalKorean);                   // 旧韩文

        var output = ReadOutput(run);
        Assert.Equal(FakeTranslation, output[3].Name);                        // 第 4 条是重译结果
    }

    // ───────── G：Baseline（首次捕获）迁移规则保持不变 ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task G_Baseline有旧中文时KR独有Key同样继承(TranslationMode mode)
    {
        var run = await RunAsync(
            mode,
            baseline: null,
            current: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                null),
            oldChinese: FourModeAgentE2EHarness.OldChinese(4),
            firstCaptureOnly: true);

        Assert.Equal(4, run.Plan.InheritCount);          // 迁移模式：有旧中文 ⇒ 全部继承
        Assert.Equal(0, run.Plan.TranslateNewCount);
        Assert.Equal(0, run.Plan.TranslateModifiedCount);
        Assert.Equal(0, run.Plan.TranslateMissingCount);
        Assert.Equal(0, run.Provider.CallCount);

        var output = ReadOutput(run);
        Assert.Equal(4, output.Count);
        Assert.Equal("旧中文4", output[3].Name);
    }

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task G2_Baseline无旧中文时KR独有Key仍缺译(TranslationMode mode)
    {
        var run = await RunAsync(
            mode,
            baseline: null,
            current: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                null),
            oldChinese: FourModeAgentE2EHarness.OldChinese(3),
            firstCaptureOnly: true);

        Assert.Equal(3, run.Plan.InheritCount);
        Assert.Equal(1, run.Plan.TranslateMissingCount);
        Assert.Equal(0, run.Plan.TranslateNewCount);
        Assert.Equal(1, run.Provider.CallCount);
        Assert.Equal(KoreanOnlyKey, Assert.Single(run.Provider.ReceivedEntries).UnitKey);
    }

    // ───────── H：EN_ONLY 完全兼容（KR 独有 Key + 旧中文不得改变英文语义） ─────────

    [Fact]
    public async Task H_ENONLY不受KR独有Key与旧中文影响()
    {
        var run = await RunAsync(
            TranslationMode.EnglishOnly,
            baseline: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                null),
            current: new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanB },   // KR 多出一条，且第 4 条变化
                new[] { EnglishC, EnglishC, EnglishC },
                null),
            oldChinese: FourModeAgentE2EHarness.OldChinese(4));

        Assert.False(run.Plan.IsKoreanAuthoritative);
        Assert.Equal(SourceLanguage.English, run.Plan.AuthoritativeLanguage);
        Assert.Equal(3, run.Plan.ExpectedOutputKeys.Count);            // 仍以英文结构为准
        Assert.DoesNotContain(KoreanOnlyKey, run.Plan.ExpectedOutputKeys);
        Assert.Equal(3, run.Plan.InheritCount);
        Assert.DoesNotContain(run.Plan.Candidates, entry => entry.Key.ToString() == KoreanOnlyKey);
        Assert.Equal(0, run.Provider.CallCount);

        // EN_ONLY 不注入任何 Canonical 字段 / Mode Salt
        Assert.All(run.Plan.Candidates, entry =>
        {
            Assert.Null(entry.CanonicalKoreanText);
            Assert.Null(entry.OldCanonicalKoreanText);
            Assert.Null(entry.SourceHashSalt);
        });

        var output = ReadOutput(run);
        Assert.Equal(3, output.Count);
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
    }

    // ───────── E2E 证据：ProviderCalls / BatchCallCount / TM 写入 ─────────

    private static Func<TranslationCacheServices, ITranslationProvider> DeepSeekFactory(
        FakeE2EBatchClient client,
        TranslationMode mode)
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
            translationMode: mode);

    [Fact]
    public async Task I_旧中文命中继承时Batch调用为0且不写TM与Cache()
    {
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null));

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(4),
            DeepSeekFactory(client, TranslationMode.KoreanEnglish));

        Assert.Empty(run.Plan.NeedTranslate);                     // 全部继承 ⇒ 无待翻译条目
        Assert.Empty(run.AgentEntries);
        Assert.Equal(0, client.BatchCallCount);                   // 真实网络请求数 = 0
        Assert.Equal(0, client.TotalItemCount);
        Assert.Equal(0, _harness.TranslationRowCount());          // 不写新的 AI TM 记录覆盖继承结果
        Assert.Equal(0, _harness.RequestCacheRowCount());         // 也不产生 request_cache 写入
    }

    [Fact]
    public async Task I2_旧中文缺失时真实链路正常调用一次Batch并写TM()
    {
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null));

        var client = new FakeE2EBatchClient();
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(3),
            DeepSeekFactory(client, TranslationMode.KoreanEnglish));

        Assert.Single(run.Plan.NeedTranslate);
        Assert.Equal(1, client.BatchCallCount);
        Assert.Equal(1, client.TotalItemCount);
        Assert.Equal(1, _harness.TranslationRowCount());          // 只有缺译那条写入 TM
    }

    [Fact]
    public void KR独有Key的旧中文来自同一份单元集合而不是重新解析()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null));

        var oldChinese = FourModeAgentE2EHarness.OldChinese(4);
        var oldChineseUnits = FourModeAgentE2EHarness.BuildOldChineseUnits(oldChinese);

        var withUnits = ProductionTranslationPlanBuilder.Build(
            TranslationMode.KoreanOnly,
            Array.Empty<DiffEntry>(),
            capture,
            harness.EnglishDirectory,
            oldChineseUnits);
        var withoutUnits = ProductionTranslationPlanBuilder.Build(
            TranslationMode.KoreanOnly,
            Array.Empty<DiffEntry>(),
            capture,
            harness.EnglishDirectory);

        Assert.Equal(4, withUnits.InheritCount);            // 有旧中文单元 ⇒ KR-only Key 也能 Inherit
        Assert.Equal(4, withoutUnits.TranslateMissingCount); // 没有 ⇒ 退化为 TranslateMissing（旧行为）
        Assert.Equal(4, oldChineseUnits.Count);
    }
}
