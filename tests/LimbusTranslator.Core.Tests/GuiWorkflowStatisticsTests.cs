using LimbusTranslator.Infrastructure.Presentation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.19轮：**载入进度后的统计刷新**（状态栏 / 按钮可用性）。
///
/// 背景（用户实测）：完成过一次汉化后「从 output 载入进度」，界面仍显示"待翻译 10448"，
/// 看起来像"什么都没载入" —— 因为 <c>NeedTranslateCount</c> 原本只在"分析完成"时写入一次。
/// 现在载入后会把**已载入（有译文）的条目**从待翻译里扣除，因此需要提供 SetNeedTranslateCount。
/// </summary>
public sealed class GuiWorkflowStatisticsTests
{
    private static GuiWorkflowState Analyzed(int needTranslate)
    {
        var state = new GuiWorkflowState();
        state.CompleteAnalyze(
            totalCount: 1000,
            inheritCount: 800,
            newCount: 10,
            modifiedCount: 50,
            missingTranslationCount: 140,
            deletedCount: 0,
            needTranslateCount: needTranslate,
            needReviewCount: 5,
            blockingIssueCount: 0,
            hasCanonicalMissing: false);
        return state;
    }

    [Fact]
    public void 分析后状态栏显示待翻译数且开始汉化可用()
    {
        var state = Analyzed(needTranslate: 200);

        Assert.Contains("待翻译：200", state.StatusBarText("韩文 + 英文", "已连接"), StringComparison.Ordinal);
        Assert.True(state.CanTranslate);
    }

    [Fact]
    public void 载入进度后可把待翻译清零_并自动禁用开始汉化()
    {
        var state = Analyzed(needTranslate: 200);

        state.SetNeedTranslateCount(0);   // 全部条目都从 output 载入到了译文

        Assert.Contains("待翻译：0", state.StatusBarText("韩文 + 英文", "已连接"), StringComparison.Ordinal);
        Assert.False(state.CanTranslate);   // 没有待翻译内容 ⇒ 按钮应禁用
    }

    [Fact]
    public void 载入部分进度后待翻译按剩余量更新()
    {
        var state = Analyzed(needTranslate: 200);

        state.SetNeedTranslateCount(37);

        Assert.Equal(37, state.NeedTranslateCount);
        Assert.True(state.CanTranslate);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-9999)]
    public void 负数必须收敛为零(int value)
    {
        var state = Analyzed(needTranslate: 10);

        state.SetNeedTranslateCount(value);

        Assert.Equal(0, state.NeedTranslateCount);
    }

    [Fact]
    public void 载入路径必须使用与翻译一致的列表口径并刷新统计()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.OutputProgress.cs"));

        // ① 逐条列表用"载入到的全部条目"（与「开始汉化」后的 SetReviewSource(selectedEntries) 同口径）
        Assert.Contains("SetReviewSource(outcome.Applied);", source);
        Assert.DoesNotContain("SetReviewSource(outcome.ForReview);", source);

        // ② 载入后刷新工作流统计（否则状态栏一直是分析时的旧值）
        Assert.Contains("ApplyLoadedProgressStatistics(plan);", source);
        Assert.Contains("_workflow.SetNeedTranslateCount(stillMissing);", source);

        // ③ 摘要里必须说明 output 的覆盖范围（含"文件缺失"）
        Assert.Contains("outcome.Loaded.Describe()", source);
        Assert.Contains("未写出的文件不会被载入", source);
    }

    [Fact]
    public void 界面必须显示载入摘要()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LimbusTranslator.Wpf", "MainWindow.xaml"));

        Assert.Contains("Text=\"{Binding OutputProgressStatusText, Mode=OneWay}\"", xaml);
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
