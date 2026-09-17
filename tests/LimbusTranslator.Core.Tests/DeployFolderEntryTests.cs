using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.9轮：**「部署到汉化文件夹」接线守卫**。
///
/// 背景：用户反馈"没有部署到汉化文件夹的按钮"。事实是：`DeployService.Deploy(outputRoot, 目标目录, backupRoot)`
/// 的目标目录**本来就是参数**，缺的只是"用户自选目录 + 同一套门禁确认"的入口。
/// 本测试用源码守卫固定这条接线，避免将来被改回"只能部署到自动定位的游戏目录"。
/// </summary>
public sealed class DeployFolderEntryTests
{
    [Fact]
    public void 界面必须提供部署到汉化文件夹按钮()
    {
        var xaml = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml");

        Assert.Contains("Click=\"DeployToFolder_Click\"", xaml);
        Assert.Contains("部署到汉化文件夹", xaml);
    }

    [Fact]
    public void 文件夹部署必须自动定位目标_且只部署本轮勾选的文件()
    {
        var codeBehind = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml.cs");
        var viewModel = ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs");

        // ① 目标目录**自动定位**（第9.0C.13轮；定位失败才回退到手动选择）
        Assert.Contains("ResolveDeployFolderAsync()", codeBehind);
        Assert.Contains("OpenFolderDialog", codeBehind);   // 仅作为自动定位失败时的回退

        // ② 确认文案与部署范围都来自**同一份任务选择**
        Assert.Contains("SelectedDeployFiles()", codeBehind);
        Assert.Contains("BuildDeployConfirmationText(targetDir, \"汉化文件夹\", restrict)", codeBehind);
        Assert.Contains("DeployToFolderAsync(targetDir)", codeBehind);

        // ③ 增量范围最终传给 DeployService（清单子集校验，不是"整目录复制"）
        Assert.Contains("IReadOnlyCollection<string>? restrictToRelativePaths", viewModel);
        Assert.Contains("DeployService.Deploy(outputRoot, targetDir, backupRoot", viewModel);

        // ④ 两个入口共用同一个实现：「部署到游戏」仍是全量、目标仍由自动定位得到
        Assert.Contains("DeployToTargetAsync(located.ChineseDir, \"游戏汉化目录\")", viewModel);
    }

    [Fact]
    public void 确认文案支持目标标签且默认仍为游戏目录()
    {
        var presentation = ReadSource("src", "LimbusTranslator.Infrastructure", "Presentation", "GuiWorkflowState.cs");

        Assert.Contains("string targetLabel = \"游戏目录\"", presentation);
        Assert.Contains("即将把汉化文件部署到{targetLabel}：", presentation);
    }

    private static string ReadSource(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepositoryRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"未找到源文件: {path}");
        return File.ReadAllText(path);
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
