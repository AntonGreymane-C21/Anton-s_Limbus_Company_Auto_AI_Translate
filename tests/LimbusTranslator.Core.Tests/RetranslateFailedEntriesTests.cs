using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Review;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.17轮：**重译「本批翻译失败」的条目**（用户需求）。
///
/// 背景：批次彻底失败（含关闭思考的降级重试）时，该批条目没有译文并被标记为待人工审核
///（结构化标记 <c>DiffEntry.ProviderBatchFailed</c>）。用户希望有一个按钮把这些条目**重翻一次**，
/// 而不是整轮重烧 API。
///
/// 本文件固定：①筛选项只命中带标记的条目；②GUI 接线；③重译只取带标记的条目、输出只写受影响文件。
/// </summary>
public sealed class RetranslateFailedEntriesTests
{
    private static DiffEntry Entry(bool failed)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "RPGSystem/rpg-loc-dial.json", RecordId = "4", FieldPath = "dataList[4].texts[6].text" },
            NewSourceText = "And just how do you expect to do that?",
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.TranslateMissing,
            ProviderBatchFailed = failed,
        };

    [Fact]
    public void 筛选项_只看本批翻译失败_只命中带标记的条目()
    {
        Assert.True(ReviewFilter.Matches(Entry(failed: true), ReviewFilterKind.ProviderFailed));
        Assert.False(ReviewFilter.Matches(Entry(failed: false), ReviewFilterKind.ProviderFailed));

        // 其它筛选项不受影响
        Assert.True(ReviewFilter.Matches(Entry(failed: true), ReviewFilterKind.All));
    }

    [Fact]
    public void 筛选项文案可解析且追加在末尾()
    {
        var kind = ReviewFilter.Parse("只看「本批翻译失败」（可重译）");

        Assert.Equal(ReviewFilterKind.ProviderFailed, kind);
        Assert.Equal(ReviewFilter.DisplayNames[^1], "只看「本批翻译失败」（可重译）");
        Assert.Equal(ReviewFilter.DisplayNames.Count, Enum.GetValues<ReviewFilterKind>().Length);
    }

    [Fact]
    public void 界面必须提供重译按钮并接到ViewModel()
    {
        var xaml = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml");
        var codeBehind = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml.cs");

        Assert.Contains("重译翻译失败的条目", xaml);
        Assert.Contains("Click=\"RetranslateFailedEntries_Click\"", xaml);
        Assert.Contains("IsEnabled=\"{Binding CanRetranslateFailed}\"", xaml);
        Assert.Contains("_viewModel.RetranslateFailedEntries()", codeBehind);
    }

    [Fact]
    public void 重译只取带标记的条目且输出限制在受影响文件()
    {
        var viewModel = ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs");

        // ① 目标条目来自结构化标记
        Assert.Contains(".Where(entry => entry.ProviderBatchFailed)", viewModel);
        Assert.Contains("_retranslateKeys = failedKeys;", viewModel);

        // ② 重译模式：只翻失败条目（忽略文件勾选）
        Assert.Contains("plan.NeedTranslate", viewModel);
        Assert.Contains(".Where(entry => forcedKeys.Contains(entry.Key.ToString()))", viewModel);

        // ③ 重译模式：只写出受影响文件（含文件内其它条目，保证输出完整）
        Assert.Contains("affectedFiles.Contains(entry.Key.RelativeFilePath)", viewModel);

        // ④ 正常模式行为不变（仍走任务选择）
        Assert.Contains("taskSelection.SelectedOutputEntries.ToList()", viewModel);
    }

    [Fact]
    public void 按钮可用性由结构化标记驱动()
    {
        var taskSelection = ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.TaskSelection.cs");
        var paging = ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.ReviewPaging.cs");

        Assert.Contains("CanRetranslateFailed = plan.OutputEntries.Any(entry => entry.ProviderBatchFailed);", taskSelection);
        Assert.Contains("CanRetranslateFailed = entries?.Any(entry => entry.ProviderBatchFailed) == true;", paging);
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
