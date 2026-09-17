using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.15轮：**文件列表交互接线守卫**（源码级）。
///
/// 背景（用户实测）：滚动文件列表时勾选框状态错乱（已勾的显示未勾、未勾的显示已勾）；
/// 需求：加右键菜单（选择/取消选择）+ Ctrl / Shift 像资源管理器一样多选。
///
/// 本测试固定三件事，防止将来被改回去：
///   ① 关闭行容器回收（VirtualizationMode=Standard）——滚动错乱的根因；
///   ② 支持 Ctrl / Shift 多选（SelectionMode=Extended + SelectionUnit=FullRow）；
///   ③ 右键菜单三项都接到 ViewModel 的批量选择方法（且批量同步用 SetSelectedSilently，
///      避免每行触发一次统计重算）。
/// </summary>
public sealed class FileTaskGridInteractionTests
{
    [Fact]
    public void 文件列表必须关闭容器回收以修复滚动时勾选状态错乱()
    {
        var xaml = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml");

        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Standard\"", xaml);
        Assert.Contains("x:Name=\"FileTasksGrid\"", xaml);
    }

    [Fact]
    public void 文件列表必须支持Ctrl与Shift多选()
    {
        var xaml = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml");

        Assert.Contains("SelectionMode=\"Extended\"", xaml);
        Assert.Contains("SelectionUnit=\"FullRow\"", xaml);
        Assert.Contains("PreviewMouseRightButtonDown=\"FileTasksGrid_PreviewMouseRightButtonDown\"", xaml);
    }

    [Fact]
    public void 右键菜单必须提供本轮处理与不处理与反选()
    {
        var xaml = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml");
        var codeBehind = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml.cs");
        var viewModel = ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.TaskSelection.cs");

        Assert.Contains("本轮处理（选中的文件）", xaml);
        Assert.Contains("本轮不处理（选中的文件）", xaml);
        Assert.Contains("Click=\"MarkFilesSelected_Click\"", xaml);
        Assert.Contains("Click=\"MarkFilesUnselected_Click\"", xaml);
        Assert.Contains("Click=\"InvertFilesSelection_Click\"", xaml);

        // 右键菜单作用于"选中行"，且批量操作走同一份权威状态
        Assert.Contains("FileTasksGrid.SelectedItems", codeBehind);
        Assert.Contains("MarkFilesSelected(SelectedFileRows())", codeBehind);
        Assert.Contains("MarkFilesUnselected(SelectedFileRows())", codeBehind);
        Assert.Contains("InvertFilesSelection(SelectedFileRows())", codeBehind);

        Assert.Contains("_fileSelection.SelectAll(files)", viewModel);
        Assert.Contains("_fileSelection.SelectNone(files)", viewModel);
        Assert.Contains("_fileSelection.Invert(files)", viewModel);

        // 批量同步必须静默（否则 N 行 = N 次统计重算，且与行虚拟化互相覆盖）
        Assert.Contains("row.SetSelectedSilently(_fileSelection.IsSelected(row.LogicalFile));", viewModel);
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
