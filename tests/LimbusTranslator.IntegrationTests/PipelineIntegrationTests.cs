using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.IntegrationTests.Support;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.IntegrationTests;

/// <summary>
/// 第6轮：核心翻译流水线端到端集成测试（Scenario A~F）。
///
/// 真实主链：Parser → Diff → ContextIndex → Coordinator → Agent → TM / Context / Fingerprint / Cache
///          → Provider(Fake) → Placeholder → Validator → TM Save → Merge → ReleaseGate → Manifest。
/// 全部使用临时目录与临时 SQLite；不访问真实 API / 真实数据库 / 真实游戏目录。
/// </summary>
[Collection(SqliteCollection.Name)]
public class PipelineIntegrationTests
{
    private const string StoryCurrentKey = "StoryData/1D101A.json|1|dataList[1].content";

    [Fact]
    public async Task ScenarioA_第一次翻译_完整主链贯通()
    {
        using var fixture = IntegrationFixture.Create();
        using var harness = PipelineHarness.Create(fixture);

        var run = await harness.RunAsync();

        // ---- Diff 分类（真实 Parser + DiffEngine）----
        var byAction = run.AllEntries.GroupBy(e => e.Action).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, byAction[TranslationAction.TranslateNew]);        // General id2 + StoryData id3
        Assert.Equal(2, byAction[TranslationAction.TranslateModified]);   // General id3 + StoryData id1
        Assert.Equal(4, byAction[TranslationAction.TranslateMissing]);    // General id2/4/5/6
        Assert.Equal(4, byAction[TranslationAction.Inherit]);             // General id1 + StoryData id0/id2 + StoryData id1.teller
        Assert.Equal(0, run.Diff.DeletedCount);
        Assert.NotEmpty(run.Diff.NewUnits);                               // 完整 NewUnits 已返回

        // ---- Provider（Fake）只被调用了“需要翻译”的两个文件批次 ----
        Assert.Equal(2, run.ProviderCalls);

        // ---- StoryData 当前句获得正确 Neighbor（未变化文本也能作邻句）----
        var storyItems = harness.AllRequestItems
            .Where(i => i.Id.StartsWith("StoryData/1D101A.json|", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, storyItems.Count);
        var current = storyItems.Single(i => i.Id == StoryCurrentKey);
        Assert.NotNull(current.Context);
        Assert.Equal("Line zero unchanged.", current.Context!.Previous!.SourceText);
        Assert.Equal("Line two unchanged.", current.Context.Next!.SourceText);
        Assert.Equal("Gregor", current.Speaker);                        // 当前句 speaker（来自 teller，已进入请求）
        Assert.Null(current.Context.Previous.Speaker);                  // 上一句是无 speaker 的旁白

        // ---- Placeholder 保护：占位符在最终译文中完整保留 ----
        var placeholderEntry = run.AllEntries.Single(e => e.Key.FieldPath == "dataList[4].name");
        Assert.NotNull(placeholderEntry.Translation);
        Assert.Contains("{0}", placeholderEntry.Translation);
        Assert.Contains("{1}", placeholderEntry.Translation);

        // ---- Validator 运行（Fake 回显英文 → 英文残留 Warning → NeedsReview）----
        Assert.Contains(placeholderEntry.ValidationIssues, i => i.Code == ValidationIssueCodes.EnglishResidue);
        Assert.True(placeholderEntry.NeedsReview);

        // ---- TM / Request Cache 均写入 ----
        Assert.Equal(8, harness.CountRows("translations"));
        Assert.True(harness.CountRows("request_cache") >= 1);

        // ---- Merge + Manifest + ReleaseGate ----
        Assert.True(File.Exists(Path.Combine(fixture.OutputDir, "General.json")));
        Assert.True(File.Exists(Path.Combine(fixture.OutputDir, "StoryData", "1D101A.json")));
        Assert.True(run.Output.IsComplete);
        Assert.True(File.Exists(OutputManifestService.GetManifestPath(fixture.OutputDir)));
        Assert.True(run.Manifest.IsComplete);
        Assert.Equal(ReleaseGateStatus.RequiresConfirmation, run.Manifest.ReleaseGateStatus);
        Assert.Equal(0, run.Manifest.ErrorCount);
        Assert.NotEmpty(run.Manifest.BlockingReasons);
        Assert.False(string.IsNullOrWhiteSpace(run.Manifest.ManifestId));
    }

    [Fact]
    public async Task ScenarioB_第二次相同运行_ExactTM优先且不调用Provider()
    {
        using var fixture = IntegrationFixture.Create();

        using (var first = PipelineHarness.Create(fixture))
        {
            var run1 = await first.RunAsync();
            Assert.Equal(2, run1.ProviderCalls);
            Assert.Equal(8, first.CountRows("translations"));
        }

        // 新的运行上下文（新 harness / 新 RunId），同一测试数据
        using var second = PipelineHarness.Create(fixture);
        var run2 = await second.RunAsync();

        // ExactUnit TM 命中 → Provider 不调用；且不会先走 Request Cache
        Assert.Equal(0, run2.ProviderCalls);
        var translated = run2.AllEntries
            .Where(e => e.Action is TranslationAction.TranslateNew
                        or TranslationAction.TranslateModified
                        or TranslationAction.TranslateMissing)
            .ToList();
        Assert.All(translated, e => Assert.Equal(TranslationMemoryMatchType.ExactUnit, e.TmMatchType));

        // TM 命中不会触碰 Provider / Cache 层：本次运行的 Trace 文件应为空（0 次 Provider 请求）
        var traceFile = second.TraceWriter.FilePath!;
        var traceLines = File.Exists(traceFile)
            ? File.ReadAllLines(traceFile).Count(l => !string.IsNullOrWhiteSpace(l))
            : 0;
        Assert.Equal(0, traceLines);

        // TM 行数不因重复运行而增长
        Assert.Equal(8, second.CountRows("translations"));
    }

    [Fact]
    public async Task ScenarioC_清空TM后_RequestCache命中且Validator重跑()
    {
        using var fixture = IntegrationFixture.Create();

        int cacheRowsAfterFirstRun;
        using (var first = PipelineHarness.Create(fixture))
        {
            await first.RunAsync();
            cacheRowsAfterFirstRun = first.CountRows("request_cache");
            Assert.True(cacheRowsAfterFirstRun >= 1);

            // 只清空 translations，保留 request_cache
            first.ClearTranslations();
            Assert.Equal(0, first.CountRows("translations"));
        }

        using var second = PipelineHarness.Create(fixture);
        var run2 = await second.RunAsync();

        // TM Miss → Request Cache Hit → Provider 不调用（用 Trace 证明命中来自缓存）
        Assert.Equal(0, run2.ProviderCalls);
        var traceLines = File.ReadAllLines(second.TraceWriter.FilePath!)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement)
            .ToList();
        Assert.Equal(2, traceLines.Count);
        Assert.All(traceLines, e => Assert.True(e.GetProperty("cacheHit").GetBoolean()));
        Assert.All(traceLines, e => Assert.False(e.GetProperty("networkCalled").GetBoolean()));

        // Cache Hit 仍然重新执行当前 Validator（英文残留 Warning 依然产生）
        var placeholderEntry = run2.AllEntries.Single(e => e.Key.FieldPath == "dataList[4].name");
        Assert.Contains(placeholderEntry.ValidationIssues, i => i.Code == ValidationIssueCodes.EnglishResidue);
        Assert.True(placeholderEntry.NeedsReview);

        // 最终重新写回 TM；缓存行数不增长
        Assert.Equal(8, second.CountRows("translations"));
        Assert.Equal(cacheRowsAfterFirstRun, second.CountRows("request_cache"));
    }

    [Fact]
    public async Task ScenarioD_Context改变_Fingerprint变化且缓存未命中()
    {
        using var fixture = IntegrationFixture.Create();

        using (var first = PipelineHarness.Create(fixture))
        {
            await first.RunAsync();
            Assert.Equal(2, first.CountRows("request_cache"));   // General 与 StoryData 各一条
            first.ClearTranslations();                            // 让下次必须到达 Request 层
        }

        // 只修改 StoryData 的 Previous（id0）源文；当前句（id1）完全不变
        fixture.ChangeStoryPreviousSource();

        using var second = PipelineHarness.Create(fixture);
        var run2 = await second.RunAsync();

        // General 请求内容未变 → Cache Hit；StoryData 上下文变化 → Cache Miss（重新调用 Provider）
        Assert.Equal(1, run2.ProviderCalls);
        Assert.Equal(3, second.CountRows("request_cache"));

        // 当前句自身文本未变（SourceHash 不变），变化的只有上下文
        var currentItem = second.AllRequestItems.Single(i => i.Id == StoryCurrentKey);
        Assert.Equal("Line one needs translation now.", currentItem.Source);
        Assert.Equal("Line zero CHANGED source.", currentItem.Context!.Previous!.SourceText);
    }


    [Fact]
    public async Task ScenarioE_HumanReviewed优先且不再调用Provider()
    {
        using var fixture = IntegrationFixture.Create();

        using (var first = PipelineHarness.Create(fixture))
        {
            await first.RunAsync();

            // 模拟人工审核：对 Missing translation item（General id4）写回 HumanReviewed
            var key = TestEntries.Key("General.json", "4", "dataList[3].name");
            var unit = new TranslationUnit
            {
                Key = key,
                FilePath = key.RelativeFilePath,
                RecordId = key.RecordId,
                FieldPath = key.FieldPath,
                SourceText = "Missing translation item two",
            };
            Assert.True(first.Memory.SaveHumanReviewed(unit, "缺失旧译的人工确认译文。"));
        }

        using var second = PipelineHarness.Create(fixture);
        var run2 = await second.RunAsync();

        // 全部条目仍在 TM 中 → Provider 不调用
        Assert.Equal(0, run2.ProviderCalls);

        var reviewed = run2.AllEntries.Single(e => e.Key.FieldPath == "dataList[3].name");
        Assert.Equal("缺失旧译的人工确认译文。", reviewed.Translation);
        Assert.Equal(TranslationSource.HumanReviewed, reviewed.Provenance);
        Assert.Equal(TranslationMemoryMatchType.ExactUnit, reviewed.TmMatchType);
        Assert.False(reviewed.NeedsReview);
    }

    [Fact]
    public async Task ScenarioB2_同一Fixture重复运行三次_结果稳定()
    {
        using var fixture = IntegrationFixture.Create();

        var signatures = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var harness = PipelineHarness.Create(fixture);
            var run = await harness.RunAsync();
            signatures.Add(DescribeRun(run));
        }

        // 第 1 次是真实翻译；第 2、3 次为 ExactUnit TM 命中 → 译文保持一致
        Assert.Equal(3, signatures.Count);
        var translationsOfFirst = signatures[0].Split('|')[..^1];
        Assert.Contains("calls=2", signatures[0]);
        Assert.Contains("calls=0", signatures[1]);
        Assert.Contains("calls=0", signatures[2]);
        Assert.NotEmpty(translationsOfFirst);
    }

    private static string DescribeRun(PipelineRunOutcome run)
        => string.Join(
            "|",
            $"calls={run.ProviderCalls}",
            $"gate={run.Manifest.ReleaseGateStatus}",
            $"files={run.Output.WrittenFileCount}",
            $"verified={run.Output.VerifiedEntryCount}");
}

