using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Services;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P3轮：生产顺序对齐验收（P1-1 / P1-2）。
///
/// 所有用例都通过 <see cref="ProductionTranslationPlanBuilder"/>（WPF / CLI / 测试**同一个生产层**）
/// 与 <see cref="FourModeAgentE2EHarness"/>（临时 SQLite / 临时 Cache / Fake Provider）执行：
///   候选（英文输出结构 + KR 独有 Key）→ ApplyToEntries（Canonical 决定最终动作）→ 按最终动作过滤 → Agent
/// 真实 DeepSeek API 调用数恒为 0。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class ProductionOrderAlignmentTests
{
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

    private static DiffEntry Entry(TranslationAction action) => new()
    {
        Key = new UnitKey { RelativeFilePath = "Items.json", RecordId = "1", FieldPath = "dataList[0].name" },
        NewSourceText = EnglishC,
        DiffKind = DiffKind.Unchanged,
        Action = action,
    };

    // ───────── A：EN_ONLY（KR 变、EN 不变、旧中文存在）→ 不进 Agent ─────────

    [Fact]
    public async Task A_ENONLY_KR变化EN不变_不进入Agent且Provider调用为0()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), KoreanChanged());
        Assert.Equal(1, capture.CanonicalDiff!.Modified);

        var provider = new FakeE2EProvider { Mode = TranslationMode.EnglishOnly };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, run.Inherit);
        Assert.Equal(0, run.TranslateModified);
        Assert.Empty(run.AgentEntries);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, run.PatchedCount);                 // EN_ONLY 不接线
        Assert.Null(Assert.Single(run.Candidates).SourceHashSalt);
    }

    // ───────── B / C / D：KR 三模式（同一份数据）→ Canonical 决定动作并进入 Agent ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish, EnglishC)]
    [InlineData(TranslationMode.KoreanJapanese, JapaneseC)]
    [InlineData(TranslationMode.KoreanOnly, KoreanB)]
    public async Task BCD_KR三模式_KR变化EN不变_Canonical决定动作且进入Agent(TranslationMode mode, string expectedSource)
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), KoreanChanged());

        var provider = new FakeE2EProvider { Mode = mode };
        var run = await harness.RunProductionChainAsync(
            mode,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, capture.CanonicalDiff!.Modified);  // 韩文确实变了
        Assert.Equal(1, run.TranslateModified);            // Canonical 覆盖了 EN Diff 的 Inherit
        Assert.Single(run.AgentEntries);                   // ⇒ 真正进入 Agent
        Assert.Equal(1, provider.CallCount);               // ⇒ ProviderCalls = 1
        Assert.Equal(1, run.PatchedCount);
        Assert.Equal(expectedSource, Assert.Single(provider.ReceivedSourceTexts));
        Assert.NotNull(Assert.Single(provider.ReceivedSourceHashSalts));
    }

    // ───────── E / F：仅参考译本变化（KR 未变）→ 保持继承，不进 Agent ─────────

    [Fact]
    public async Task E_KR未变仅英文参考变化_KREN_继承且不进入Agent()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), ReferenceOnlyEnglishChanged());

        Assert.Equal(0, capture.CanonicalDiff!.Modified);
        Assert.Equal(1, capture.CanonicalDiff.EnglishChangedCount);

        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish };
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            _ => provider);

        Assert.Equal(1, run.Inherit);
        Assert.Equal(0, run.TranslateModified);
        Assert.Empty(run.AgentEntries);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task F_KR未变仅日文参考变化_KRJP_继承且不进入Agent()
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
    }

    // ───────── G：ApplyToEntries 把 TranslateModified 降级为 Inherit → 不再需要翻译 ─────────

    [Fact]
    public void G_接线把动作降级为继承_过滤后不再需要翻译且旧中文保持()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), ReferenceOnlyEnglishChanged());

        // 英文输出结构：英文确实变了 ⇒ TranslateModified（Translation 为空且旧中文存在）
        var entries = FourModeAgentE2EHarness.BuildEnglishCandidates(
            new[] { EnglishC },
            new[] { "旧中文1" },
            new[] { "Hello changed" }).ToList();

        var entry = Assert.Single(entries);
        Assert.Equal(TranslationAction.TranslateModified, entry.Action);
        Assert.Null(entry.Translation);

        // 生产层：接线（Canonical：KR 未变 + 旧中文存在 ⇒ Inherit）→ 按最终动作过滤
        // 第9.0B-P4轮：KR 模式的候选来自**当前韩文**（新对象），英文结构只提供旧中文 / 说话人 / 顺序
        var plan = ProductionTranslationPlanBuilder.Build(
            TranslationMode.KoreanEnglish, entries, capture, harness.EnglishDirectory);

        var planned = Assert.Single(plan.OutputEntries);
        Assert.Equal(1, plan.InheritCount);
        Assert.Empty(plan.NeedTranslate);                    // 过滤发生在接线之后 ⇒ 0 条进入 Agent
        Assert.Equal(1, plan.InheritedKeptCount);
        Assert.Equal("旧中文1", planned.Translation);        // 旧中文保持（不是 AI 译文）
        Assert.Equal(TranslationSource.Inherited, planned.Provenance);
        Assert.Equal(FourModeAgentE2EHarness.KeyOf(0), planned.Key.ToString());   // 韩文权威 Key
        Assert.True(plan.IsKoreanAuthoritative);
    }

    // ───────── H / I：首次 Canonical 基线（100 条 / 缺旧中文） ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task H_基线100条旧中文齐全_全部继承且Provider调用为0(TranslationMode mode)
    {
        using var harness = new FourModeAgentE2EHarness();
        harness.WriteAll(new E2ESources(
            FourModeAgentE2EHarness.Texts(100, "원문"),
            FourModeAgentE2EHarness.Texts(100, "Source "),
            null));
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
        Assert.Equal(0, provider.CallCount);
    }

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task I_基线缺旧中文_TranslateMissing正常进入Agent(TranslationMode mode)
    {
        using var harness = new FourModeAgentE2EHarness();
        harness.WriteAll(new E2ESources(
            FourModeAgentE2EHarness.Texts(3, "새원문"),
            FourModeAgentE2EHarness.Texts(3, "New source "),
            null));
        var capture = harness.Capture();
        Assert.True(capture.IsFirstCanonicalBaseline);

        var provider = new FakeE2EProvider { Mode = mode };
        var run = await harness.RunProductionChainAsync(
            mode,
            capture,
            oldChinese: null,
            providerFactory: _ => provider);

        Assert.Equal(3, run.TranslateMissing);
        Assert.Equal(0, run.Inherit);
        Assert.Equal(3, run.AgentEntries.Count);
        Assert.Equal(1, provider.CallCount);
    }

    // ───────── J：真实 Provider 链路上的 BatchCallCount 证据（接线后继承 ⇒ 0 次网络请求） ─────────

    [Fact]
    public async Task J_接线后转为继承_真实Provider链路BatchCallCount为0()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), ReferenceOnlyEnglishChanged());

        var client = new FakeE2EBatchClient();
        var run = await harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish,
            capture,
            FourModeAgentE2EHarness.OldChinese(1),
            services => new DeepSeekTranslationProvider(
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
                translationMode: TranslationMode.KoreanEnglish),
            oldEnglish: new[] { EnglishC },
            newEnglish: new[] { "Hello changed" });

        Assert.Empty(run.AgentEntries);                     // 接线后是 Inherit ⇒ 不进 Agent
        Assert.Equal(0, client.BatchCallCount);             // ⇒ 真实网络请求数 = 0
        Assert.Equal(0, client.TotalItemCount);
        Assert.Equal(0, harness.RequestCacheRowCount());    // 也没有产生缓存写入
        Assert.Equal("旧中文1", Assert.Single(run.Candidates).Translation);
    }

    // ───────── 生产层单元：谓词 / 顺序 / 无捕获回退 / KR 独有 Key / 删除条目 ─────────

    [Fact]
    public void 共享谓词_只接受三种需要翻译的动作()
    {
        Assert.True(ProductionTranslationPlanBuilder.IsTranslationRequired(Entry(TranslationAction.TranslateNew)));
        Assert.True(ProductionTranslationPlanBuilder.IsTranslationRequired(Entry(TranslationAction.TranslateModified)));
        Assert.True(ProductionTranslationPlanBuilder.IsTranslationRequired(Entry(TranslationAction.TranslateMissing)));
        Assert.False(ProductionTranslationPlanBuilder.IsTranslationRequired(Entry(TranslationAction.Inherit)));
        Assert.False(ProductionTranslationPlanBuilder.IsTranslationRequired(Entry(TranslationAction.SkipDeleted)));
        Assert.False(ProductionTranslationPlanBuilder.IsTranslationRequired(null));
    }

    [Fact]
    public void ENONLY_不接线且动作与英文Diff完全一致()
    {
        using var harness = new FourModeAgentE2EHarness();
        var capture = harness.CaptureAfterBaseline(Baseline(), KoreanChanged());   // 韩文确实变了
        var entries = FourModeAgentE2EHarness.BuildEnglishCandidates(
            new[] { EnglishC }, new[] { "旧中文1" }, new[] { EnglishC }).ToList();

        var plan = ProductionTranslationPlanBuilder.Build(
            TranslationMode.EnglishOnly, entries, capture, harness.EnglishDirectory);

        Assert.True(plan.HasCanonicalCapture);
        Assert.Equal(0, plan.PatchedCount);          // EN_ONLY：ApplyToEntries 内部直接返回
        Assert.Equal(1, plan.InheritCount);
        Assert.Empty(plan.NeedTranslate);
        Assert.Null(entries[0].SourceHashSalt);
        Assert.Null(entries[0].CanonicalKoreanText);
        Assert.Null(entries[0].OldCanonicalKoreanText);
    }

    [Fact]
    public void 没有Canonical捕获时_退回英文Diff动作语义()
    {
        using var harness = new FourModeAgentE2EHarness();
        var entries = FourModeAgentE2EHarness.BuildEnglishCandidates(
            new[] { EnglishC }, null, new[] { "Hello changed" }).ToList();

        var plan = ProductionTranslationPlanBuilder.Build(
            TranslationMode.KoreanEnglish, entries, capture: null, harness.EnglishDirectory);

        Assert.False(plan.HasCanonicalCapture);
        Assert.Equal(0, plan.PatchedCount);
        Assert.Equal(1, plan.TranslateModifiedCount);
        Assert.Single(plan.NeedTranslate);
    }

    [Fact]
    public void 仅存在于韩文树的Key_也必须成为候选并进入Agent()
    {
        using var harness = new FourModeAgentE2EHarness();

        // 英文输出结构为空（没有英文树），韩文树里存在该 Key 且发生了变化
        var capture = harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA }, null, null),
            new E2ESources(new[] { KoreanB }, null, null));

        var plan = ProductionTranslationPlanBuilder.Build(
            TranslationMode.KoreanOnly, Array.Empty<DiffEntry>(), capture, harness.EnglishDirectory);

        Assert.Equal(1, plan.KoreanOnlyCount);
        Assert.Equal(1, plan.TranslateModifiedCount);
        Assert.Single(plan.NeedTranslate);
    }

    [Fact]
    public void 已删除条目_既不进入候选也不进入Agent()
    {
        using var harness = new FourModeAgentE2EHarness();
        var deleted = new DiffEntry
        {
            Key = FourModeAgentE2EHarness.UnitKeyOf(0),
            NewSourceText = null,
            OldSourceText = EnglishC,
            OldTranslation = "旧中文1",
            DiffKind = DiffKind.Deleted,
            Action = TranslationAction.SkipDeleted,
        };

        var plan = ProductionTranslationPlanBuilder.Build(
            TranslationMode.KoreanEnglish, new[] { deleted }, capture: null, harness.EnglishDirectory);

        Assert.Empty(plan.Candidates);
        Assert.Empty(plan.NeedTranslate);
    }

    // ───────── 接线守卫：WPF / CLI 必须调用同一个生产层（第9.0C.1轮：WPF 分析改走唯一后台服务） ─────────

    [Fact]
    public void WPF与CLI都调用同一个生产层且不再自己接线或内联过滤()
    {
        var root = FindRepositoryRoot();
        var wpf = File.ReadAllText(Path.Combine(root, "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs"));
        var wpfGui = File.ReadAllText(Path.Combine(root, "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.Gui.cs"));
        var cli = File.ReadAllText(Path.Combine(root, "src", "LimbusTranslator.Cli", "Program.cs"));
        var analyzeService = File.ReadAllText(Path.Combine(
            root, "src", "LimbusTranslator.Infrastructure", "Services", "ProductionAnalyzeService.cs"));

        // ① 唯一生产层（Build / TryCapture）必须存在：WPF 通过后台分析服务调用，CLI 直接调用
        Assert.Contains("ProductionTranslationPlanBuilder.Build(", wpf);
        // 第9.0C.3轮：动作谓词仍然只有一份实现；WPF 通过「任务选择层」消费它，CLI 直接使用。
        var wpfTaskSelection = File.ReadAllText(Path.Combine(
            root, "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.TaskSelection.cs"));
        var taskSelectionCore = File.ReadAllText(Path.Combine(
            root, "src", "LimbusTranslator.Infrastructure", "Presentation", "TaskSelection.cs"));
        Assert.Contains("ProductionTranslationPlanBuilder.IsTranslationRequired", taskSelectionCore);
        Assert.Contains("TaskSelection.Resolve(", wpfTaskSelection);
        Assert.Contains("TaskSelection.BuildSummaries(", wpfTaskSelection);
        Assert.Contains("ResolveTaskSelection(", wpf);
        Assert.Contains("ProductionAnalyzeService", wpf + wpfGui);              // WPF 分析入口 = 后台服务
        Assert.Contains("ProductionTranslationPlanBuilder.Build(", analyzeService);
        Assert.Contains("ProductionTranslationPlanBuilder.TryCapture(", analyzeService);
        Assert.Contains("ProductionTranslationPlanBuilder.Build(", cli);
        Assert.Contains("ProductionTranslationPlanBuilder.TryCapture(", cli);
        Assert.Contains("ProductionTranslationPlanBuilder.IsTranslationRequired", cli);

        // ② 调用方不得自己拼接线顺序，也不得复制动作过滤谓词
        foreach (var (name, text) in new[] { ("WPF", wpf), ("WPF.Gui", wpfGui), ("CLI", cli) })
        {
            Assert.DoesNotContain("MultilingualSnapshotCapture.ApplyToEntries(", text);
            Assert.DoesNotContain("is TranslationAction.TranslateNew", text);
            Assert.DoesNotContain("or TranslationAction.TranslateModified", text);
        }
    }

    /// <summary>从测试输出目录向上定位仓库根（以 LimbusTranslator.slnx 为标志）。</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LimbusTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（LimbusTranslator.slnx）");
    }
}
