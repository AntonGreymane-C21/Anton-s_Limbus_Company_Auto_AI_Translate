using LimbusTranslator.Infrastructure.Services;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.22轮：**提取清单**（`ExtractManifest`）—— 让"重开程序后接着上次提取的那批文件干活"成为可能。
/// 只保存逻辑路径；损坏 / 缺失一律视为"没有清单"，绝不抛异常。
/// </summary>
public sealed class ExtractManifestTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "LT_Manifest_" + Guid.NewGuid().ToString("N"));

    public ExtractManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // 清理失败可忽略
        }
    }

    [Fact]
    public void 写入后可读回_且去重()
    {
        ExtractManifest.Save(_dir, new[] { "StoryData/A.json", "StoryData/B.json", "StoryData/A.json" });

        var loaded = ExtractManifest.TryLoad(_dir);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.Contains("StoryData/A.json", loaded);
        Assert.True(File.Exists(Path.Combine(_dir, ExtractManifest.FileName)));
    }

    [Fact]
    public void 没有清单时返回空()
    {
        Assert.Null(ExtractManifest.TryLoad(_dir));
        Assert.Null(ExtractManifest.TryLoad(string.Empty));
    }

    [Fact]
    public void 清单损坏时返回空且不抛异常()
    {
        File.WriteAllText(Path.Combine(_dir, ExtractManifest.FileName), "{ 这不是合法 JSON");

        Assert.Null(ExtractManifest.TryLoad(_dir));
    }

    [Fact]
    public void 清单里没有有效文件时返回空()
    {
        File.WriteAllText(Path.Combine(_dir, ExtractManifest.FileName), """{"files":[]}""");

        Assert.Null(ExtractManifest.TryLoad(_dir));
    }

    [Fact]
    public void 载入进度_按勾选提取_与提取清单接线守卫()
    {
        var viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs"));
        var taskSelection = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.TaskSelection.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LimbusTranslator.Wpf", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LimbusTranslator.Wpf", "MainWindow.xaml.cs"));

        // ① 提取按当前勾选执行（与翻译/部署同源）
        Assert.Contains("var selection = ResolveTaskSelection();", viewModel);
        Assert.Contains("restrictToRelativePaths: selectedFiles", viewModel);

        // ② 提取前清空 pending；③ 提取后全局收敛勾选；④ 写提取清单
        Assert.Contains("CleanupPendingDirectory(pendingDir)", viewModel);
        Assert.Contains("ConvergeSelectionToExtractedFiles(extractResult.LogicalFiles)", viewModel);
        Assert.Contains("_fileSelection.SelectNone(_allFileTasks.Select(row => row.LogicalFile));", viewModel);
        Assert.Contains("ExtractManifest.Save(pendingDir, extractResult.LogicalFiles", viewModel);

        // ⑤ 恢复勾选入口（界面按钮 + ViewModel 方法）
        Assert.Contains("Content=\"按上次提取恢复勾选\"", xaml);
        Assert.Contains("Click=\"RestoreExtractSelection_Click\"", xaml);
        Assert.Contains("_viewModel.RestoreSelectionFromLastExtract()", codeBehind);
        Assert.Contains("ExtractManifest.TryLoad(pendingDir)", viewModel);
        Assert.Contains("ExtractManifest.TryLoad(Path.Combine(FindProjectRoot(), \"data\", \"work\", \"pending\"))", taskSelection);
    }

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
