using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.24轮：**输出范围修复 + 回写不再卡死** 的源码守卫。
///
/// 两个真实故障：
///   ① 「审核后重新输出」把**未勾选文件**也重新写出，并把它们计入输出清单 / 发布门禁 / 待审核统计
///      —— 根因是恢复路径用"分类勾选"而不是"任务选择（分类 ∩ 文件勾选）"；
///   ② 同一按钮先把**本轮全部译文**逐条回写 TM（每条一个新连接+事务、UI 线程）⇒ 界面长时间假死。
/// </summary>
public sealed class OutputScopeAndPersistGuardTests
{
    private static string MainViewModel()
        => ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs");

    [Fact]
    public void 恢复与重新输出必须使用任务选择而不是分类勾选()
    {
        var source = MainViewModel();

        // ① 范围来自任务选择（分类 ∩ 文件勾选），且空选择必须拒绝
        Assert.Contains("var selection = ResolveTaskSelection();", source);
        Assert.Contains("selection.SelectedOutputEntries.Count == 0", source);
        Assert.Contains("没有勾选任何要处理的文件", source);

        // ② Merge / Gate / 人工改写的范围都用"选中文件集合"
        Assert.Contains("var selectedFiles = selection.SelectedOutputEntries", source);
        Assert.Contains(".Where(entry => selectedFiles.Contains(entry.Key.RelativeFilePath))", source);
        Assert.Contains("r.Translation is not null && selectedFiles.Contains(r.Key.RelativeFilePath)", source);

        // ③ 旧实现（只看分类勾选）必须彻底消失
        Assert.DoesNotContain("selectedCategories", source);
    }

    [Fact]
    public void 人工审核回写必须只回写人工确认条目且走后台单事务()
    {
        var source = MainViewModel();

        // ① 只回写 Provenance == HumanReviewed 的条目（不再是"本轮全部译文"）
        Assert.Contains("entry.Provenance == TranslationSource.HumanReviewed", source);
        Assert.Contains("PersistHumanReviewedEntriesAsync", source);

        // ② 后台 + 单事务批量写（不再逐条 SaveReviewedEntries 整个集合、也不在 UI 线程）
        Assert.Contains("HumanReviewService.SaveReviewedEntriesBulk(targets, memory)", source);
        Assert.DoesNotContain("HumanReviewService.SaveReviewedEntries(_reviewAllEntries.ToList(), memory)", source);

        // ③ 没有可回写条目时立即返回（不空跑）
        Assert.Contains("没有需要回写的条目", source);
    }

    [Fact]
    public void 批量确认也必须使用单事务批量写回()
    {
        var review = ReadSource("src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.Review.cs");

        Assert.Contains("var saved = SaveHumanReviewedBulk(candidates);", review);
        Assert.Contains("HumanReviewService.SaveReviewedEntriesBulk(entries, memory)", review);
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
