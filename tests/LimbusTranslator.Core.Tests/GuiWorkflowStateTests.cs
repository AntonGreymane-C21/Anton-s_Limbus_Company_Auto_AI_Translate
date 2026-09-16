using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C轮：GUI 工作流状态（按钮可用性 / 模式锁定 / 审核进度 / 部署确认与失败状态）。
/// </summary>
public sealed class GuiWorkflowStateTests
{
    [Fact]
    public void 初始状态_显示四步引导且不能翻译()
    {
        var state = new GuiWorkflowState();

        Assert.True(state.ShowFirstRunGuide);
        Assert.Contains("选择游戏目录", state.FirstRunGuideText);
        Assert.False(state.CanTranslate);            // 尚未分析
        Assert.False(state.CanReview);
        Assert.False(state.CanDeploy);
        Assert.True(state.CanAnalyze);
        Assert.True(state.CanChangeTranslationMode);
        Assert.Equal("空闲", state.PhaseText);
    }

    [Fact]
    public void 分析运行中_禁用冲突操作并锁定模式()
    {
        var state = new GuiWorkflowState();
        state.BeginAnalyze();

        Assert.True(state.IsBusy);
        Assert.False(state.CanAnalyze);               // 不能重复分析
        Assert.False(state.CanChangeTranslationMode); // 不能改模式
        Assert.False(state.CanTranslate);
        Assert.False(state.CanDeploy);
        Assert.False(state.CanGenerateOutput);
        Assert.False(state.ShowFirstRunGuide);
        Assert.Equal("正在分析更新", state.PhaseText);
    }

    [Fact]
    public void 分析完成_写入后端统计并允许翻译()
    {
        var state = new GuiWorkflowState();
        state.BeginAnalyze();
        state.CompleteAnalyze(
            totalCount: 120, inheritCount: 60, newCount: 20, modifiedCount: 10,
            missingTranslationCount: 30, deletedCount: 5, needTranslateCount: 60,
            needReviewCount: 18, blockingIssueCount: 0, hasCanonicalMissing: true);
        state.SetGate(ReleaseGateStatus.RequiresConfirmation);

        Assert.True(state.HasAnalysis);
        Assert.False(state.ShowFirstRunGuide);
        Assert.True(state.CanTranslate);
        Assert.True(state.CanReview);
        Assert.True(state.CanGenerateOutput);
        Assert.True(state.CanChangeTranslationMode);   // 非运行中 ⇒ 可改
        Assert.Equal(60, state.InheritCount);
        Assert.Equal(18, state.NeedReviewCount);
        Assert.True(state.HasCanonicalMissing);
        Assert.True(state.RequiresDeployConfirmation);
        Assert.True(state.CanDeploy);                  // RequiresConfirmation 仍可部署（由对话框确认）
    }

    [Fact]
    public void 无需翻译时不启动Provider()
    {
        var state = new GuiWorkflowState();
        state.BeginAnalyze();
        state.CompleteAnalyze(10, 10, 0, 0, 0, 0, needTranslateCount: 0, needReviewCount: 0, blockingIssueCount: 0, hasCanonicalMissing: false);

        Assert.False(state.CanTranslate);
        Assert.False(state.HasTranslated);
    }

    [Fact]
    public void 翻译运行中_禁用分析部署改模式()
    {
        var state = Analyzed(needTranslate: 5);
        state.SetGate(ReleaseGateStatus.Passed);
        state.BeginTranslate();

        Assert.True(state.IsBusy);
        Assert.False(state.CanAnalyze);
        Assert.False(state.CanTranslate);
        Assert.False(state.CanDeploy);
        Assert.False(state.CanChangeTranslationMode);
        Assert.False(state.CanGenerateOutput);
        Assert.Equal("正在翻译", state.PhaseText);
    }

    [Fact]
    public void 翻译完成_恢复可操作并标记已生成译文()
    {
        var state = Analyzed(needTranslate: 5);
        state.BeginTranslate();
        state.CompleteTranslate();

        Assert.False(state.IsBusy);
        Assert.True(state.HasTranslated);
        Assert.True(state.CanAnalyze);
        Assert.True(state.CanChangeTranslationMode);
        Assert.Contains("已生成候选译文", state.StatusBarText("韩文 + 英文", "已连接"));
    }

    [Fact]
    public void 运行失败_回到空闲并给出可读原因()
    {
        var state = Analyzed(needTranslate: 5);
        state.BeginTranslate();
        state.Fail("API 返回 401：API Key 无效或已过期");

        Assert.False(state.IsBusy);
        Assert.Equal("API 返回 401：API Key 无效或已过期", state.LastErrorSummary);
        Assert.True(state.CanAnalyze);
    }

    [Fact]
    public void 审核进度_区分已接受与已修改()
    {
        var state = Analyzed(needReview: 3);
        state.NoteReviewed("A", ReviewOutcome.Accepted);
        state.NoteReviewed("B", ReviewOutcome.Edited);

        Assert.Equal(2, state.ReviewedCount);
        Assert.Equal(1, state.AcceptedCount);
        Assert.Equal(1, state.EditedCount);
        Assert.Equal(1, state.PendingReviewCount);
        Assert.Contains("已审核 2 / 待审核 3", state.ReviewProgressText);
    }

    [Fact]
    public void 门禁阻塞_禁止部署()
    {
        var state = Analyzed(needTranslate: 1);
        state.SetGate(ReleaseGateStatus.Blocked);

        Assert.False(state.CanDeploy);
        Assert.False(state.RequiresDeployConfirmation);
    }

    [Fact]
    public void 门禁状态_中文映射与下一步提示()
    {
        Assert.Equal("检查通过", WorkflowText.GateStatus(ReleaseGateStatus.Passed));
        Assert.Equal("需要人工确认", WorkflowText.GateStatus(ReleaseGateStatus.RequiresConfirmation));
        Assert.Equal("存在阻塞问题", WorkflowText.GateStatus(ReleaseGateStatus.Blocked));
        Assert.Contains("可以直接生成输出", WorkflowText.GateNextStep(ReleaseGateStatus.Passed));
        Assert.Contains("待审核", WorkflowText.GateNextStep(ReleaseGateStatus.RequiresConfirmation));
    }

    [Fact]
    public void 部署确认_必须展示目标文件数与备份回滚说明()
    {
        var text = DeployPresentation.BuildConfirmationText(
            @"D:\Steam\Limbus Company\LimbusCompany_Data\Lang\LLC_zh-CN",
            fileCount: 2100,
            requiresConfirmation: true,
            gateStatus: ReleaseGateStatus.RequiresConfirmation);

        Assert.Contains(@"D:\Steam\Limbus Company", text);
        Assert.Contains("2100", text);
        Assert.Contains("自动备份", text);
        Assert.Contains("回滚", text);
        Assert.Contains("需要人工确认", text);
        Assert.Contains("确认要继续部署吗", text);
    }

    [Fact]
    public void 部署成功_给出备份位置与文件数()
    {
        var result = new DeployResult
        {
            DeployedCount = 2100,
            BackedUpCount = 12,
            BackupDir = @"D:\backup\20260916",
            TargetDir = @"D:\Game\Lang\LLC_zh-CN",
            Files = new[] { "Items.json" },
            Status = DeploymentStatus.Succeeded,
        };

        var (summary, isCritical) = DeployPresentation.BuildOutcome(result);

        Assert.False(isCritical);
        Assert.Contains("部署完成", summary);
        Assert.Contains("2100", summary);
        Assert.Contains(@"D:\backup\20260916", summary);
    }

    [Fact]
    public void 部署失败已回滚_说明恢复状态且非高危()
    {
        var result = new DeployResult
        {
            DeployedCount = 3,
            BackupDir = @"D:\backup\1",
            TargetDir = @"D:\Game\Lang\LLC_zh-CN",
            Files = new[] { "a.json" },
            Status = DeploymentStatus.FailedRolledBack,
            RolledBackCount = 3,
            Errors = new[] { "写盘失败：磁盘空间不足" },
        };

        var (summary, isCritical) = DeployPresentation.BuildOutcome(result);

        Assert.False(isCritical);
        Assert.Contains("已回滚到部署前状态", summary);
        Assert.Contains("游戏目录已恢复到部署前的状态", summary);
        Assert.Contains("磁盘空间不足", summary);
    }

    [Fact]
    public void 部署失败回滚不完整_标记为高危并列出未恢复文件()
    {
        var result = new DeployResult
        {
            DeployedCount = 3,
            BackupDir = @"D:\backup\1",
            TargetDir = @"D:\Game\Lang\LLC_zh-CN",
            Files = new[] { "a.json" },
            Status = DeploymentStatus.FailedRollbackIncomplete,
            RolledBackCount = 1,
            UnrecoveredRelativePaths = new[] { "Lang/LLC_zh-CN/a.json", "Lang/LLC_zh-CN/b.json" },
            RollbackErrors = new[] { "回滚失败：文件被占用" },
        };

        var (summary, isCritical) = DeployPresentation.BuildOutcome(result);

        Assert.True(isCritical);
        Assert.Contains("回滚未全部完成", summary);
        Assert.Contains("a.json", summary);
        Assert.Contains("文件被占用", summary);
    }

    private static GuiWorkflowState Analyzed(int needTranslate = 0, int needReview = 0)
    {
        var state = new GuiWorkflowState();
        state.BeginAnalyze();
        state.CompleteAnalyze(
            totalCount: needTranslate + needReview,
            inheritCount: 0,
            newCount: needTranslate,
            modifiedCount: 0,
            missingTranslationCount: 0,
            deletedCount: 0,
            needTranslateCount: needTranslate,
            needReviewCount: needReview,
            blockingIssueCount: 0,
            hasCanonicalMissing: false);
        return state;
    }
}
