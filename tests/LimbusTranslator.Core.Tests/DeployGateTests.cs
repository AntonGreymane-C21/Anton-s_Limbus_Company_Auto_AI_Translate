using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第3轮：DeployService 的发布门禁二次硬检查测试。
/// 全部在临时目录中进行，绝不写入真实游戏目录。
/// </summary>
public class DeployGateTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_DEPLOY_GATE_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch
            {
                // 清理失败可忽略（临时目录）
            }
        }
    }

    private static OutputRunManifest WriteManifest(string output, ReleaseGateStatus status)
    {
        var reasons = status switch
        {
            ReleaseGateStatus.Blocked => new[]
            {
                new ReleaseGateReason
                {
                    Kind = ReleaseGateReasonKinds.NewTranslationHardError,
                    Code = ValidationIssueCodes.PlaceholderMismatch,
                    Severity = ValidationSeverity.Error,
                    Provenance = TranslationSource.AI,
                    Count = 1,
                    Escalation = ReleaseGateStatus.Blocked,
                    Message = "AI 译文存在 1 条结构安全错误（PLACEHOLDER_MISMATCH），禁止直接部署。",
                },
            },
            ReleaseGateStatus.RequiresConfirmation => new[]
            {
                new ReleaseGateReason
                {
                    Kind = ReleaseGateReasonKinds.HistoricalInheritedError,
                    Code = ValidationIssueCodes.TagMismatch,
                    Severity = ValidationSeverity.Error,
                    Provenance = TranslationSource.Inherited,
                    Count = 8,
                    Escalation = ReleaseGateStatus.RequiresConfirmation,
                    Message = "发现 8 条历史继承译文存在结构安全差异（TAG_MISMATCH）。",
                },
            },
            _ => Array.Empty<ReleaseGateReason>(),
        };

        var gate = new ReleaseGateResult
        {
            Status = status,
            EvaluatedEntryCount = 1,
            ErrorCount = status == ReleaseGateStatus.Passed ? 0 : 1,
            WarningCount = 0,
            NeedsReviewCount = 0,
            BlockingErrorCount = status == ReleaseGateStatus.Blocked ? 1 : 0,
            HistoricalInheritedErrorCount = status == ReleaseGateStatus.RequiresConfirmation ? 8 : 0,
            Reasons = reasons,
        };

        return OutputManifestService.Save(
            output,
            new OutputMergeResult
            {
                RequestedFileCount = 1,
                RequestedEntryCount = 1,
                WrittenEntryCount = 1,
                VerifiedEntryCount = 1,
                Files = new[] { "Enemies.json" },
                Issues = Array.Empty<OutputMergeIssue>(),
            },
            gate);
    }

    private static (string Output, string Target, string Backup) Prepare()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
        File.WriteAllText(Path.Combine(target, "Enemies.json"), "{\"old\":true}");
        return (output, target, backup);
    }

    [Fact]
    public void 缺少清单_部署被拒绝()
    {
        var (output, target, backup) = Prepare();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => DeployService.Deploy(output, target, backup));

            Assert.Contains("未找到输出清单", ex.Message);
            Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(target, "Enemies.json")));
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    [Fact]
    public void Blocked清单_部署被拒绝()
    {
        var (output, target, backup) = Prepare();
        try
        {
            var manifest = WriteManifest(output, ReleaseGateStatus.Blocked);
            var ex = Assert.Throws<InvalidOperationException>(() => DeployService.Deploy(output, target, backup));

            Assert.Contains("发布门禁阻断", ex.Message);
            Assert.Contains("PLACEHOLDER_MISMATCH", ex.Message);
            Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(target, "Enemies.json")));
            Assert.Equal(ReleaseGateStatus.Blocked, manifest.ReleaseGateStatus);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    [Fact]
    public void RequiresConfirmation未确认_部署被拒绝()
    {
        var (output, target, backup) = Prepare();
        try
        {
            WriteManifest(output, ReleaseGateStatus.RequiresConfirmation);

            var ex = Assert.Throws<InvalidOperationException>(() => DeployService.Deploy(output, target, backup));

            Assert.Contains("需要人工确认", ex.Message);
            Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(target, "Enemies.json")));
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    [Fact]
    public void RequiresConfirmation已确认当前清单_允许部署()
    {
        var (output, target, backup) = Prepare();
        try
        {
            var manifest = WriteManifest(output, ReleaseGateStatus.RequiresConfirmation);
            OutputManifestService.SaveConfirmation(output, manifest.ManifestId, 0, 8);

            var result = DeployService.Deploy(output, target, backup);

            Assert.Equal(1, result.DeployedCount);
            Assert.Equal(ReleaseGateStatus.RequiresConfirmation, result.ReleaseGateStatus);
            Assert.Equal(manifest.ManifestId, result.ManifestId);
            Assert.Equal("{\"dataList\":[]}", File.ReadAllText(Path.Combine(target, "Enemies.json")));
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    [Fact]
    public void Passed清单_允许部署()
    {
        var (output, target, backup) = Prepare();
        try
        {
            WriteManifest(output, ReleaseGateStatus.Passed);

            var result = DeployService.Deploy(output, target, backup);

            Assert.Equal(1, result.DeployedCount);
            Assert.Equal(ReleaseGateStatus.Passed, result.ReleaseGateStatus);
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }

    [Fact]
    public void 新清单的旧确认_部署被拒绝()
    {
        var (output, target, backup) = Prepare();
        try
        {
            var first = WriteManifest(output, ReleaseGateStatus.RequiresConfirmation);
            OutputManifestService.SaveConfirmation(output, first.ManifestId, 0, 8);

            // 重新生成输出：新清单 Id，确认记录消失
            WriteManifest(output, ReleaseGateStatus.RequiresConfirmation);

            var ex = Assert.Throws<InvalidOperationException>(() => DeployService.Deploy(output, target, backup));

            Assert.Contains("需要人工确认", ex.Message);
            Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(target, "Enemies.json")));
        }
        finally
        {
            Cleanup(output, target, backup);
        }
    }
}

