using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.20轮：**载入进度的双来源（output + 翻译记忆）与「生成完整快照」** 接线守卫。
///
/// 背景：output 只包含"此前写出过"的文件（Merge 只写本轮勾选范围），因此仅靠 output 载入永远是部分进度；
/// TM 才是完整累计进度。现在载入 = output ∪ TM；若仍不全，可点「生成完整快照」把全部权威文件写出后再载入。
/// </summary>
public sealed class LoadProgressAndSnapshotTests
{
    private static string LoadProgressSource()
        => ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.OutputProgress.cs");

    [Fact]
    public void 载入进度必须同时使用output与翻译记忆两个来源()
    {
        var source = LoadProgressSource();

        // ① 对"output 没覆盖到"的条目按当前模式盐算哈希 → 批量查 TM
        Assert.Contains("SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText!, entry.SourceHashSalt)", source);
        Assert.Contains("memory.FindExactUnits(wanted)", source);
        Assert.Contains("TM 补充候选", source);

        // ② 摘要必须分别报告两个来源（否则用户无法判断覆盖范围）
        Assert.Contains("OutputLoadedCount", source);
        Assert.Contains("TmSupplementedCount", source);
        Assert.Contains("翻译记忆补充", source);
    }

    [Fact]
    public void 从TM补充的条目不得回写TM以保留原始来源()
    {
        var source = LoadProgressSource();

        Assert.Contains(".Where(entry => !tmLoadedKeys.Contains(entry.Key.ToString()))", source);
        Assert.Contains("pair.Entry.Provenance = hit.Source;", source);
    }

    [Fact]
    public void 界面必须提供生成完整快照按钮()
    {
        var xaml = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml");
        var codeBehind = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml.cs");

        Assert.Contains("生成完整快照", xaml);
        Assert.Contains("Click=\"GenerateFullSnapshot_Click\"", xaml);
        Assert.Contains("_viewModel.GenerateFullSnapshot()", codeBehind);
    }

    [Fact]
    public void 完整快照必须写出全部权威文件并随后载入()
    {
        var source = LoadProgressSource();

        // ① 范围 = 计划里的**全部**输出条目（不是任务选择的范围）
        Assert.Contains("var allEntries = plan.OutputEntries;", source);
        Assert.Contains("MergeAndRecordOutput(allEntries, translations, \"完整快照\", plan)", source);

        // ② 生成后立刻"抓进来"（复用同一条载入路径，且不重复切换 IsBusy）
        Assert.Contains("await LoadProgressFromOutputCoreAsync(manageProgress: false);", source);

        // ③ 不调用 API（用当前计划里已有译文）
        Assert.Contains("Coordinator.CollectTranslations(allEntries)", source);
    }

    [Fact]
    public void 载入主体必须可被快照流程复用()
    {
        var source = LoadProgressSource();

        Assert.Contains("private async Task<bool> LoadProgressFromOutputCoreAsync(bool manageProgress)", source);
        Assert.Contains("await LoadProgressFromOutputCoreAsync(manageProgress: true);", source);
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
