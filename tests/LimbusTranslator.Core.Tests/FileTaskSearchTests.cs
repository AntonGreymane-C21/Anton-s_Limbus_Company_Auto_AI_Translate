using LimbusTranslator.Infrastructure.Presentation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.18轮：**任务范围界面的文件列表搜索框**。
///
/// 语义（用户需求）：像待审核页一样能按文件名快速过滤（例如只看 `StoryData/S10*`），
/// 且**只影响呈现**——未显示的文件仍然保持它原本的"本轮处理 / 本轮不处理"状态。
/// </summary>
public sealed class FileTaskSearchTests
{
    [Theory]
    [InlineData("StoryData/S1015B.json", "s1015b")]      // 忽略大小写
    [InlineData("StoryData/S1015B.json", "StoryData/S")]
    [InlineData("RPGSystem/rpg-loc-dial.json", "rpg-loc")]
    [InlineData("StoryData/S1015B.json", "  S1015B  ")]   // 首尾空白会被裁掉
    public void 搜索命中_忽略大小写且支持片段(string file, string search)
        => Assert.True(TaskSelection.MatchesFileSearch(file, search));

    [Theory]
    [InlineData("StoryData/S1015B.json", "S9999")]
    [InlineData("Bufs.json", "StoryData")]
    public void 搜索不命中(string file, string search)
        => Assert.False(TaskSelection.MatchesFileSearch(file, search));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 空搜索_全部命中(string? search)
        => Assert.True(TaskSelection.MatchesFileSearch("StoryData/S1015B.json", search));

    [Fact]
    public void 搜索非空而文件名为空_不命中()
        => Assert.False(TaskSelection.MatchesFileSearch(null, "S10"));

    [Fact]
    public void 界面必须提供搜索框与清除按钮且接到ViewModel()
    {
        var xaml = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml");
        var codeBehind = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml.cs");
        var viewModel = ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.TaskSelection.cs");

        Assert.Contains("Text=\"{Binding FileTaskSearchText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"", xaml);
        Assert.Contains("Click=\"ClearFileTaskSearch_Click\"", xaml);
        Assert.Contains("_viewModel.ClearFileTaskSearch()", codeBehind);

        // 实时过滤 + 单一匹配实现 + 搜索提示
        Assert.Contains("TaskSelection.MatchesFileSearch(row.LogicalFile, _fileTaskSearchText)", viewModel);
        Assert.Contains("ApplyTaskScopeFilter(log: false);", viewModel);
        Assert.Contains("仅显示匹配的", viewModel);
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
