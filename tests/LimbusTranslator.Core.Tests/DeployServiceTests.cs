using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 部署服务测试。
/// </summary>
public class DeployServiceTests
{
    /// <summary>写入一份 Passed 门禁清单（第3轮起部署的前置条件）。</summary>
    private static void SaveWithPassingGate(string output, OutputMergeResult result)
        => OutputManifestService.Save(output, result, PassingGate());

    /// <summary>构造 Passed 门禁结果（测试用）。</summary>
    private static ReleaseGateResult PassingGate() => new()
    {
        Status = ReleaseGateStatus.Passed,
        EvaluatedEntryCount = 1,
        ErrorCount = 0,
        WarningCount = 0,
        NeedsReviewCount = 0,
        BlockingErrorCount = 0,
        HistoricalInheritedErrorCount = 0,
        Reasons = Array.Empty<ReleaseGateReason>(),
    };

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 部署_应复制文件并备份原文件()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();

        // 源：输出目录含 1 个汉化文件
        // 第3轮：部署必须有输出清单（ReleaseGate 检查前提），此处写入一份 Passed 清单
        SaveWithPassingGate(output, new OutputMergeResult
        {
            RequestedFileCount = 1,
            RequestedEntryCount = 1,
            WrittenEntryCount = 1,
            VerifiedEntryCount = 1,
            Files = new[] { "Enemies.json" },
            Issues = Array.Empty<OutputMergeIssue>(),
        });
        File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
        // 目标：已有旧文件
        File.WriteAllText(Path.Combine(target, "Enemies.json"), "{\"dataList\":[{\"old\":true}]}");

        var result = DeployService.Deploy(output, target, backup);

        Assert.Equal(1, result.DeployedCount);
        Assert.Equal(1, result.BackedUpCount);

        // 目标已更新
        var deployed = File.ReadAllText(Path.Combine(target, "Enemies.json"));
        Assert.Equal("{\"dataList\":[]}", deployed);

        // 备份保留了旧内容
        var backedUp = File.ReadAllText(Path.Combine(result.BackupDir, "Enemies.json"));
        Assert.Equal("{\"dataList\":[{\"old\":true}]}", backedUp);

        Directory.Delete(output, true);
        Directory.Delete(target, true);
        Directory.Delete(backup, true);
    }

    [Fact]
    public void 部署_输出为空_应抛异常()
    {
        var output = MakeTempDir();   // 空目录
        var target = MakeTempDir();
        var backup = MakeTempDir();

        Assert.Throws<InvalidOperationException>(() =>
            DeployService.Deploy(output, target, backup));

        Directory.Delete(output, true);
        Directory.Delete(target, true);
        Directory.Delete(backup, true);
    }

    [Fact]
    public void 部署_目标不存在_应抛异常()
    {
        var output = MakeTempDir();
        File.WriteAllText(Path.Combine(output, "Enemies.json"), "{}");
        var backup = MakeTempDir();
        var notExist = Path.Combine(Path.GetTempPath(), "LT_NoDir_" + Guid.NewGuid().ToString("N"));

        Assert.Throws<DirectoryNotFoundException>(() =>
            DeployService.Deploy(output, notExist, backup));

        Directory.Delete(output, true);
        Directory.Delete(backup, true);
    }

    [Fact]
    public void 部署_存在输出清单时_只部署本轮已核验文件()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();

        File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
        // 模拟 output 中遗留的旧文件；它不在本轮清单内，不能被部署。
        File.WriteAllText(Path.Combine(output, "Stale.json"), "{\"new\":true}");
        File.WriteAllText(Path.Combine(target, "Enemies.json"), "{\"old\":true}");
        File.WriteAllText(Path.Combine(target, "Stale.json"), "{\"oldStale\":true}");

        SaveWithPassingGate(output, new OutputMergeResult
        {
            RequestedFileCount = 1,
            RequestedEntryCount = 1,
            WrittenEntryCount = 1,
            VerifiedEntryCount = 1,
            Files = new[] { "Enemies.json" },
            Issues = Array.Empty<OutputMergeIssue>(),
        });

        var result = DeployService.Deploy(output, target, backup);

        Assert.True(result.UsedOutputManifest);
        Assert.True(result.IsOutputComplete);
        Assert.Equal(new[] { "Enemies.json" }, result.Files);
        Assert.Equal("{\"dataList\":[]}", File.ReadAllText(Path.Combine(target, "Enemies.json")));
        Assert.Equal("{\"oldStale\":true}", File.ReadAllText(Path.Combine(target, "Stale.json")));

        Directory.Delete(output, true);
        Directory.Delete(target, true);
        Directory.Delete(backup, true);
    }

    [Fact]
    public void 部署_输出清单不完整时_应拒绝部署()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
        File.WriteAllText(Path.Combine(target, "Enemies.json"), "{\"old\":true}");

        SaveWithPassingGate(output, new OutputMergeResult
        {
            RequestedFileCount = 1,
            RequestedEntryCount = 2,
            WrittenEntryCount = 0,
            VerifiedEntryCount = 0,
            Files = Array.Empty<string>(),
            Issues = new[]
            {
                new OutputMergeIssue
                {
                    Kind = OutputMergeIssueKind.MissingTranslation,
                    RelativeFilePath = "Enemies.json",
                    Message = "缺少译文。",
                },
            },
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeployService.Deploy(output, target, backup));

        Assert.Contains("已拒绝部署", ex.Message);
        Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(target, "Enemies.json")));

        Directory.Delete(output, true);
        Directory.Delete(target, true);
        Directory.Delete(backup, true);
    }
}
