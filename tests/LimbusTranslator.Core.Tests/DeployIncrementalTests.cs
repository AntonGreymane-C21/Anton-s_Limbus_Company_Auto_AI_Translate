using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.13轮：**增量部署**（只部署本轮「任务范围」勾选的文件）。
///
/// 背景（用户实测）：点「部署到汉化文件夹」时确认框显示"即将复制的文件数: 944"，
/// 而本轮只勾选了 26 个文件 —— 旧实现调 <c>DeployService.Deploy</c> 未传任何范围，
/// 于是把**输出清单里的全部文件**都复制过去。
///
/// 现在的语义（用户确认的方案 A）：
///   - 「部署到游戏」= 全量（不传范围，行为与历史一致）；
///   - 「部署到汉化文件夹」= 只部署「清单 ∩ 本轮勾选文件」，且范围必须是清单的子集。
/// </summary>
public sealed class DeployIncrementalTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_IncDeploy_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void SaveManifest(string output, string[] files)
        => OutputManifestService.Save(output, new OutputMergeResult
        {
            RequestedFileCount = files.Length,
            RequestedEntryCount = files.Length,
            WrittenEntryCount = files.Length,
            VerifiedEntryCount = files.Length,
            Files = files,
            Issues = Array.Empty<OutputMergeIssue>(),
        }, new ReleaseGateResult
        {
            Status = ReleaseGateStatus.Passed,
            EvaluatedEntryCount = 1,
            ErrorCount = 0,
            WarningCount = 0,
            NeedsReviewCount = 0,
            BlockingErrorCount = 0,
            HistoricalInheritedErrorCount = 0,
            Reasons = Array.Empty<ReleaseGateReason>(),
        });

    private static void WriteOutputFile(string output, string relative, string content)
    {
        var path = Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void 增量部署_只复制选中文件_其他文件不被触碰()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        try
        {
            var files = new[] { "Bufs.json", "StoryData/1D101A.json", "StoryData/S949A.json" };
            SaveManifest(output, files);
            foreach (var file in files)
            {
                WriteOutputFile(output, file, "{\"new\":true}");
            }

            // 目标目录里已经有旧版本（其中 Bufs.json 本轮**未勾选**，必须保持原样）
            WriteOutputFile(target, "Bufs.json", "{\"old\":true}");
            WriteOutputFile(target, "StoryData/S949A.json", "{\"old\":true}");

            var result = DeployService.Deploy(
                output, target, backup, restrictToRelativePaths: new[] { "StoryData/1D101A.json" });

            Assert.Equal(1, result.DeployedCount);
            Assert.Equal("{\"new\":true}", File.ReadAllText(Path.Combine(target, "StoryData", "1D101A.json")));

            // 未勾选的文件：内容与存在性都不变
            Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(target, "Bufs.json")));
            Assert.Equal("{\"old\":true}", File.ReadAllText(Path.Combine(target, "StoryData", "S949A.json")));
        }
        finally
        {
            Directory.Delete(output, true);
            Directory.Delete(target, true);
            Directory.Delete(backup, true);
        }
    }

    [Fact]
    public void 增量部署_不传范围_行为与全量一致()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        try
        {
            var files = new[] { "A.json", "B.json", "StoryData/C.json" };
            SaveManifest(output, files);
            foreach (var file in files)
            {
                WriteOutputFile(output, file, "{\"new\":true}");
            }

            var result = DeployService.Deploy(output, target, backup);   // 「部署到游戏」走这条

            Assert.Equal(3, result.DeployedCount);
            Assert.True(File.Exists(Path.Combine(target, "A.json")));
            Assert.True(File.Exists(Path.Combine(target, "B.json")));
            Assert.True(File.Exists(Path.Combine(target, "StoryData", "C.json")));
        }
        finally
        {
            Directory.Delete(output, true);
            Directory.Delete(target, true);
            Directory.Delete(backup, true);
        }
    }

    [Fact]
    public void 增量部署_范围为空_必须拒绝()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        try
        {
            SaveManifest(output, new[] { "A.json" });
            WriteOutputFile(output, "A.json", "{}");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                DeployService.Deploy(output, target, backup, restrictToRelativePaths: Array.Empty<string>()));

            Assert.Contains("增量部署范围为空", ex.Message);
            Assert.False(File.Exists(Path.Combine(target, "A.json")));   // 未写入任何文件
        }
        finally
        {
            Directory.Delete(output, true);
            Directory.Delete(target, true);
            Directory.Delete(backup, true);
        }
    }

    [Fact]
    public void 增量部署_范围不在输出清单内_必须拒绝()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        try
        {
            SaveManifest(output, new[] { "A.json" });
            WriteOutputFile(output, "A.json", "{}");

            var ex = Assert.Throws<InvalidOperationException>(() =>
                DeployService.Deploy(output, target, backup, restrictToRelativePaths: new[] { "NotVerified.json" }));

            Assert.Contains("不在本次输出清单内", ex.Message);
            Assert.False(File.Exists(Path.Combine(target, "A.json")));
        }
        finally
        {
            Directory.Delete(output, true);
            Directory.Delete(target, true);
            Directory.Delete(backup, true);
        }
    }

    [Fact]
    public void 增量部署_路径大小写不敏感()
    {
        var output = MakeTempDir();
        var target = MakeTempDir();
        var backup = MakeTempDir();
        try
        {
            SaveManifest(output, new[] { "StoryData/S949A.json" });
            WriteOutputFile(output, "StoryData/S949A.json", "{\"new\":true}");

            var result = DeployService.Deploy(
                output, target, backup, restrictToRelativePaths: new[] { "storydata/s949a.json" });

            Assert.Equal(1, result.DeployedCount);
        }
        finally
        {
            Directory.Delete(output, true);
            Directory.Delete(target, true);
            Directory.Delete(backup, true);
        }
    }
}
