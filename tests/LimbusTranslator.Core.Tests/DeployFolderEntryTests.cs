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
    public void 文件夹部署必须走同一个确认与部署入口()
    {
        var codeBehind = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml.cs");
        var viewModel = ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs");

        // ① 先选择目录 → 再用同一套确认文案（目标标签为"所选汉化文件夹"）→ 才执行部署
        Assert.Contains("OpenFolderDialog", codeBehind);
        Assert.Contains("BuildDeployConfirmationText(targetDir, \"所选汉化文件夹\")", codeBehind);
        Assert.Contains("DeployToFolderAsync(targetDir)", codeBehind);

        // ② 两个入口共用同一个部署实现，且 DeployService 的目标目录是真参数
        Assert.Contains("DeployToTargetAsync(located.ChineseDir, \"游戏汉化目录\")", viewModel);
        Assert.Contains("DeployToTargetAsync(targetDirectory, \"所选汉化文件夹\")", viewModel);
        Assert.Contains("DeployService.Deploy(outputRoot, targetDir, backupRoot)", viewModel);
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
