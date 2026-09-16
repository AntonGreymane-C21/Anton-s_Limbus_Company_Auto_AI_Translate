using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第3轮：输出清单门禁信息、人工确认绑定与 DeployService 二次硬检查测试。
/// 全部在临时目录中进行，绝不写入真实游戏目录。
/// </summary>
public class ReleaseGateManifestTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_GATE_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>写入一份带指定门禁结论的清单。</summary>
    private static OutputRunManifest SaveManifest(
        string output,
        ReleaseGateStatus status,
        string[]? files = null,
        int warnings = 0,
        int historicalInheritedErrors = 0)
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
                    Kind = ReleaseGateReasonKinds.NeedsReview,
                    Code = "*",
                    Severity = ValidationSeverity.Warning,
                    Count = 1,
                    Escalation = ReleaseGateStatus.RequiresConfirmation,
                    Message = "存在 1 条未审核结果（待人工确认）。",
                },
            },
            _ => Array.Empty<ReleaseGateReason>(),
        };

        var gate = new ReleaseGateResult
        {
            Status = status,
            EvaluatedEntryCount = 1,
            ErrorCount = status == ReleaseGateStatus.Blocked ? 1 : 0,
            WarningCount = warnings,
            NeedsReviewCount = status == ReleaseGateStatus.RequiresConfirmation ? 1 : 0,
            BlockingErrorCount = status == ReleaseGateStatus.Blocked ? 1 : 0,
            HistoricalInheritedErrorCount = historicalInheritedErrors,
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
                Files = files ?? new[] { "Enemies.json" },
                Issues = Array.Empty<OutputMergeIssue>(),
            },
            gate);
    }

    [Fact]
    public void 清单_门禁状态与计数可序列化并回读()
    {
        var output = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
            var saved = SaveManifest(output, ReleaseGateStatus.RequiresConfirmation, warnings: 3, historicalInheritedErrors: 8);

            var loaded = OutputManifestService.Load(output);

            Assert.NotNull(loaded);
            Assert.Equal(OutputManifestVersions.Current, loaded.FormatVersion);
            Assert.Equal(saved.ManifestId, loaded.ManifestId);
            Assert.Equal(ReleaseGateStatus.RequiresConfirmation, loaded.ReleaseGateStatus);
            Assert.Equal(3, loaded.WarningCount);
            Assert.Equal(8, loaded.HistoricalInheritedErrorCount);
            Assert.Equal(1, loaded.NeedsReviewCount);
            Assert.False(loaded.IsLegacy);
            Assert.Null(loaded.Confirmation);
            Assert.False(loaded.HasValidConfirmation);
            // JSON 中使用字符串枚举，便于人工排查
            var json = File.ReadAllText(OutputManifestService.GetManifestPath(output));
            Assert.Contains("RequiresConfirmation", json);
        }
        finally
        {
            Cleanup(output);
        }
    }

    [Fact]
    public void 重新保存输出_旧确认自动失效()
    {
        var output = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
            var first = SaveManifest(output, ReleaseGateStatus.RequiresConfirmation, warnings: 2);
            OutputManifestService.SaveConfirmation(output, first.ManifestId, 2, 0);

            var confirmed = OutputManifestService.Load(output);
            Assert.NotNull(confirmed);
            Assert.True(confirmed.HasValidConfirmation);

            // 重新生成输出 → 新清单（新 ManifestId）→ 旧确认不得自动生效
            var second = SaveManifest(output, ReleaseGateStatus.RequiresConfirmation, warnings: 2);
            Assert.NotEqual(first.ManifestId, second.ManifestId);

            var reloaded = OutputManifestService.Load(output);
            Assert.NotNull(reloaded);
            Assert.Null(reloaded.Confirmation);
            Assert.False(reloaded.HasValidConfirmation);
        }
        finally
        {
            Cleanup(output);
        }
    }

    [Fact]
    public void 确认_必须绑定当前清单Id()
    {
        var output = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
            SaveManifest(output, ReleaseGateStatus.RequiresConfirmation);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                OutputManifestService.SaveConfirmation(output, "不存在的清单Id", 1, 0));

            Assert.Contains("已失效", ex.Message);
        }
        finally
        {
            Cleanup(output);
        }
    }

    [Fact]
    public void Blocked清单_不能写入人工确认()
    {
        var output = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
            var manifest = SaveManifest(output, ReleaseGateStatus.Blocked);

            var ex = Assert.Throws<InvalidOperationException>(() =>
                OutputManifestService.SaveConfirmation(output, manifest.ManifestId, 0, 0));

            Assert.Contains("不能通过人工确认绕过", ex.Message);
        }
        finally
        {
            Cleanup(output);
        }
    }

    [Fact]
    public void 旧版清单_归一化为Blocked并说明原因()
    {
        var output = MakeTempDir();
        try
        {
            File.WriteAllText(Path.Combine(output, "Enemies.json"), "{\"dataList\":[]}");
            var legacyJson = """
                {
                  "FormatVersion": 1,
                  "CreatedAtUtc": "2026-08-31T00:00:00Z",
                  "IsComplete": true,
                  "RequestedFileCount": 1,
                  "RequestedEntryCount": 1,
                  "WrittenFileCount": 1,
                  "VerifiedEntryCount": 1,
                  "IssueCount": 0,
                  "Files": ["Enemies.json"]
                }
                """;
            File.WriteAllText(OutputManifestService.GetManifestPath(output), legacyJson);

            var loaded = OutputManifestService.Load(output);

            Assert.NotNull(loaded);
            Assert.True(loaded.IsLegacy);
            Assert.Equal(ReleaseGateStatus.Blocked, loaded.ReleaseGateStatus);
            Assert.Contains(loaded.BlockingReasons, r => r.Contains("旧版输出清单"));
            Assert.Contains(loaded.GateReasons, r => r.Kind == ReleaseGateReasonKinds.LegacyManifest);
            Assert.False(loaded.HasValidConfirmation);
        }
        finally
        {
            Cleanup(output);
        }
    }

    [Fact]
    public void 不支持的清单版本_读取抛异常()
    {
        var output = MakeTempDir();
        try
        {
            File.WriteAllText(OutputManifestService.GetManifestPath(output), "{\"FormatVersion\": 99, \"Files\": []}");

            var ex = Assert.Throws<InvalidOperationException>(() => OutputManifestService.Load(output));
            Assert.Contains("输出清单读取失败", ex.Message);
        }
        finally
        {
            Cleanup(output);
        }
    }
}

