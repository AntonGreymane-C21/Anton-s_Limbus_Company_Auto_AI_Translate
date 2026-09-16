using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.IntegrationTests.Support;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.IntegrationTests;

/// <summary>
/// 第6轮：ReleaseGate 与 Deploy（Scenario F、G）。
/// 部署只使用临时 GameRoot；不触碰真实游戏目录。
/// </summary>
[Collection(SqliteCollection.Name)]
public class GateAndDeployIntegrationTests
{
    [Fact]
    public void ScenarioF_ReleaseGate四种状态_来源感知策略在主链有效()
    {
        using var fixture = IntegrationFixture.Create();
        var pipeline = ValidationPipeline.CreateDefault(fixture.ConfigDir);

        // ---- Clean → Passed ----
        var clean = TestEntries.Entry("General.json", "10", "dataList[9].name", "Unchanged item", "未变化条目。");
        pipeline.ValidateAndApply(clean);
        Assert.Equal(ReleaseGateStatus.Passed, ReleaseGate.Evaluate(new[] { clean }).Status);

        // ---- 启发式 Warning → RequiresConfirmation ----
        var warning = TestEntries.Entry("General.json", "11", "dataList[10].name", "Deal 20 damage.", "造成伤害。");
        pipeline.ValidateAndApply(warning);
        Assert.Equal(ReleaseGateStatus.RequiresConfirmation, ReleaseGate.Evaluate(new[] { warning }).Status);

        // ---- AI 硬安全 Error（占位符丢失）→ Blocked ----
        var aiError = TestEntries.Entry("General.json", "12", "dataList[11].name", "Deal {0} damage.", "造成伤害。");
        pipeline.ValidateAndApply(aiError);
        var blocked = ReleaseGate.Evaluate(new[] { aiError });
        Assert.Equal(ReleaseGateStatus.Blocked, blocked.Status);
        Assert.Equal(1, blocked.BlockingErrorCount);
        Assert.Contains(blocked.BlockingReasons, r => r.Contains("禁止直接部署"));

        // ---- 历史继承结构差异 → RequiresConfirmation（不 Block）----
        var inherited = TestEntries.Entry(
            "StoryData/1D101A.json",
            "9",
            "dataList[9].content",
            "<b>Hit</b> the enemy.",
            "对敌人造成伤害。",
            TranslationSource.Inherited);
        pipeline.ValidateAndApply(inherited);
        var inheritedGate = ReleaseGate.Evaluate(new[] { inherited });
        Assert.Equal(ReleaseGateStatus.RequiresConfirmation, inheritedGate.Status);
        Assert.Equal(1, inheritedGate.HistoricalInheritedErrorCount);
        Assert.Equal(0, inheritedGate.BlockingErrorCount);
    }

    [Fact]
    public async Task ScenarioG_部署门禁_只写临时GameRoot()
    {
        using var fixture = IntegrationFixture.Create();
        using var harness = PipelineHarness.Create(fixture);
        var run = await harness.RunAsync();

        // 真实运行的门禁状态：有 Warning → RequiresConfirmation
        Assert.Equal(ReleaseGateStatus.RequiresConfirmation, run.Manifest.ReleaseGateStatus);

        // 未确认 → 拒绝
        var refuse = Assert.Throws<InvalidOperationException>(() =>
            DeployService.Deploy(fixture.OutputDir, fixture.GameChineseDir, fixture.BackupDir));
        Assert.Contains("需要人工确认", refuse.Message);
        Assert.False(File.Exists(Path.Combine(fixture.GameChineseDir, "General.json")));

        // 确认当前 Manifest → 允许部署
        var manifest = OutputManifestService.Load(fixture.OutputDir)!;
        OutputManifestService.SaveConfirmation(
            fixture.OutputDir,
            manifest.ManifestId,
            manifest.WarningCount,
            manifest.HistoricalInheritedErrorCount);

        var deployed = DeployService.Deploy(fixture.OutputDir, fixture.GameChineseDir, fixture.BackupDir);
        Assert.Equal(2, deployed.DeployedCount);
        Assert.True(File.Exists(Path.Combine(fixture.GameChineseDir, "General.json")));
        Assert.True(File.Exists(Path.Combine(fixture.GameChineseDir, "StoryData", "1D101A.json")));

        // Blocked → 永远拒绝（即使存在旧确认）：先经过真实 Validator 产生硬安全 Error
        var pipeline = ValidationPipeline.CreateDefault(fixture.ConfigDir);
        var blockedEntry = TestEntries.Entry("General.json", "12", "dataList[11].name", "Deal {0} damage.", "造成伤害。");
        pipeline.ValidateAndApply(blockedEntry);
        var blockedGate = ReleaseGate.Evaluate(new[] { blockedEntry });
        Assert.Equal(ReleaseGateStatus.Blocked, blockedGate.Status);
        OutputManifestService.Save(
            fixture.OutputDir,
            new OutputMergeResult
            {
                RequestedFileCount = 1,
                RequestedEntryCount = 1,
                WrittenEntryCount = 1,
                VerifiedEntryCount = 1,
                Files = new[] { "General.json" },
                Issues = Array.Empty<OutputMergeIssue>(),
            },
            blockedGate);

        var blockedRefuse = Assert.Throws<InvalidOperationException>(() =>
            DeployService.Deploy(fixture.OutputDir, fixture.GameChineseDir, fixture.BackupDir));
        Assert.Contains("发布门禁阻断", blockedRefuse.Message);

        // Passed → 允许部署（另建临时 output / game 目录）
        var passedOutput = Path.Combine(fixture.Root, "output-passed");
        var passedGame = Path.Combine(fixture.Root, "game-passed");
        Directory.CreateDirectory(passedOutput);
        Directory.CreateDirectory(passedGame);
        File.WriteAllText(Path.Combine(passedOutput, "General.json"), "{\"dataList\":[]}");
        var passedGate = ReleaseGate.Evaluate(new[]
        {
            TestEntries.Entry("General.json", "10", "dataList[9].name", "Unchanged item", "未变化条目。"),
        });
        Assert.Equal(ReleaseGateStatus.Passed, passedGate.Status);
        OutputManifestService.Save(
            passedOutput,
            new OutputMergeResult
            {
                RequestedFileCount = 1,
                RequestedEntryCount = 1,
                WrittenEntryCount = 1,
                VerifiedEntryCount = 1,
                Files = new[] { "General.json" },
                Issues = Array.Empty<OutputMergeIssue>(),
            },
            passedGate);

        var passedDeploy = DeployService.Deploy(passedOutput, passedGame, fixture.BackupDir);
        Assert.Equal(1, passedDeploy.DeployedCount);
        Assert.Equal(ReleaseGateStatus.Passed, passedDeploy.ReleaseGateStatus);
    }
}
