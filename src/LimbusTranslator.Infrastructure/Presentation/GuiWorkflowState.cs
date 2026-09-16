using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>GUI 当前阶段（第9.0C轮；第9.0C.1轮新增 <see cref="Cancelling"/>）。</summary>
public enum GuiPhase
{
    Idle,
    Analyzing,
    Translating,
    GeneratingOutput,
    Deploying,

    /// <summary>用户已请求取消，后台尚未真正退出（此期间禁止任何新操作与模式切换）。</summary>
    Cancelling,
}

/// <summary>审核结果分类（用于「已审核 / 已接受 / 已修改 / 未审核」进度展示）。</summary>
public enum ReviewOutcome
{
    Accepted,
    Edited,
}

/// <summary>
/// GUI 工作流状态机（第9.0C轮）：**按钮可用性 / 模式锁定 / 进度 / 空状态引导**的唯一事实来源。
///
/// 设计约束：
///   - 纯状态 + 计数，不调用任何后端能力（后端结果由调用方通过 <see cref="SetAnalysis"/> /
///     <see cref="SetGate"/> 等写入）；
///   - WPF ViewModel 只读取本状态（不在 View 层重复判断「运行中要不要禁用按钮」）；
///   - 单测即可覆盖「Analyze/Translate 运行中禁用冲突操作」「模式锁定」「无需翻译时不启动 Provider」等规则。
/// </summary>
public sealed class GuiWorkflowState
{
    private readonly HashSet<string> _reviewedUnitKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReviewOutcome> _reviewOutcomes = new(StringComparer.Ordinal);

    /// <summary>当前阶段。</summary>
    public GuiPhase Phase { get; private set; } = GuiPhase.Idle;

    /// <summary>是否已完成至少一次分析（决定「开始翻译」是否可用、是否显示空状态引导）。</summary>
    public bool HasAnalysis { get; private set; }

    /// <summary>是否真的产生过 Provider 调用机会（用于「无需翻译时不调用 Provider」提示）。</summary>
    public bool HasTranslated { get; private set; }

    /// <summary>最近一次失败的简短原因（面向用户）。</summary>
    public string? LastErrorSummary { get; private set; }

    /// <summary>最近一次操作是否被用户取消（用于「操作已取消」而不是 Error）。</summary>
    public bool LastOperationCancelled { get; private set; }

    /// <summary>正在执行耗时操作（含「正在取消」阶段）。</summary>
    public bool IsBusy => Phase != GuiPhase.Idle;

    /// <summary>是否处于「已请求取消，后台尚未退出」状态。</summary>
    public bool IsCancelling => Phase == GuiPhase.Cancelling;

    /// <summary>
    /// 当前是否有可取消的操作（分析 / 翻译 / 生成 / 部署中）。
    /// 第9.0C.1轮：取消期间返回 false（禁止重复点击取消）。
    /// </summary>
    public bool CanCancel => Phase is GuiPhase.Analyzing or GuiPhase.Translating
        or GuiPhase.GeneratingOutput or GuiPhase.Deploying;

    // ---------- 分析结果计数（由后端统计写入） ----------

    public int TotalCount { get; private set; }
    public int InheritCount { get; private set; }
    public int NewCount { get; private set; }
    public int ModifiedCount { get; private set; }
    public int MissingTranslationCount { get; private set; }
    public int DeletedCount { get; private set; }
    public int NeedTranslateCount { get; private set; }
    public int NeedReviewCount { get; private set; }
    public int BlockingIssueCount { get; private set; }

    /// <summary>本次运行是否出现过韩文原文缺失（KR 模式统计）。</summary>
    public bool HasCanonicalMissing { get; private set; }

    /// <summary>发布门禁状态（未分析时为 null）。</summary>
    public ReleaseGateStatus? GateStatus { get; private set; }

    // ---------- 操作可用性 ----------

    /// <summary>运行中禁止切换翻译模式（Run 完成后重新允许）。</summary>
    public bool CanChangeTranslationMode => !IsBusy;

    public bool CanAnalyze => !IsBusy;

    /// <summary>有需要翻译的条目且不在运行中 ⇒ 才能开始翻译（否则不该启动 Provider）。</summary>
    public bool CanTranslate => !IsBusy && HasAnalysis && NeedTranslateCount > 0;

    public bool CanReview => HasAnalysis && !IsBusy;

    public bool CanGenerateOutput => !IsBusy && HasAnalysis;

    public bool CanSyncGlossary => !IsBusy;

    /// <summary>部署可用性：不在运行中、已完成分析、门禁不是 Blocked（需确认时由对话框承担）。</summary>
    public bool CanDeploy => !IsBusy && HasAnalysis && GateStatus != ReleaseGateStatus.Blocked;

    /// <summary>是否需要用户先确认再部署（门禁 RequiresConfirmation）。</summary>
    public bool RequiresDeployConfirmation => GateStatus == ReleaseGateStatus.RequiresConfirmation;

    /// <summary>首次打开（未分析且空闲）⇒ 显示四步引导。</summary>
    public bool ShowFirstRunGuide => !HasAnalysis && !IsBusy;

    // ---------- 审核进度 ----------

    public int AcceptedCount => _reviewOutcomes.Values.Count(outcome => outcome == ReviewOutcome.Accepted);

    public int EditedCount => _reviewOutcomes.Values.Count(outcome => outcome == ReviewOutcome.Edited);

    /// <summary>已处理（接受或修改）的条目数。</summary>
    public int ReviewedCount => _reviewedUnitKeys.Count;

    /// <summary>仍未处理的需要审核条目数。</summary>
    public int PendingReviewCount => Math.Max(0, NeedReviewCount - ReviewedCount);

    public string ReviewProgressText
        => NeedReviewCount == 0
            ? "没有需要人工确认的条目"
            : $"已审核 {ReviewedCount} / 待审核 {NeedReviewCount}（已接受 {AcceptedCount} / 已修改 {EditedCount}）";

    // ---------- 状态流转 ----------

    public void BeginAnalyze()
    {
        Phase = GuiPhase.Analyzing;
        LastErrorSummary = null;
        LastOperationCancelled = false;
    }

    /// <summary>用户请求取消：进入 Cancelling（禁止任何新操作，等后台退出）。</summary>
    public void BeginCancel()
    {
        if (CanCancel)
        {
            Phase = GuiPhase.Cancelling;
        }
    }

    /// <summary>取消完成：回到空闲；**不写 Error**（取消不是失败）。</summary>
    public void CompleteCancel()
    {
        Phase = GuiPhase.Idle;
        LastOperationCancelled = true;
        LastErrorSummary = null;
    }

    /// <summary>分析完成：写入后端统计。</summary>
    public void CompleteAnalyze(
        int totalCount,
        int inheritCount,
        int newCount,
        int modifiedCount,
        int missingTranslationCount,
        int deletedCount,
        int needTranslateCount,
        int needReviewCount,
        int blockingIssueCount,
        bool hasCanonicalMissing)
    {
        TotalCount = totalCount;
        InheritCount = inheritCount;
        NewCount = newCount;
        ModifiedCount = modifiedCount;
        MissingTranslationCount = missingTranslationCount;
        DeletedCount = deletedCount;
        NeedTranslateCount = needTranslateCount;
        NeedReviewCount = needReviewCount;
        BlockingIssueCount = blockingIssueCount;
        HasCanonicalMissing = hasCanonicalMissing;
        HasAnalysis = true;
        Phase = GuiPhase.Idle;

        _reviewedUnitKeys.Clear();
        _reviewOutcomes.Clear();
    }

    public void BeginTranslate() => Phase = GuiPhase.Translating;
    public void CompleteTranslate()
    {
        HasTranslated = true;
        Phase = GuiPhase.Idle;
    }

    public void BeginGenerateOutput() => Phase = GuiPhase.GeneratingOutput;

    public void CompleteGenerateOutput() => Phase = GuiPhase.Idle;

    public void BeginDeploy() => Phase = GuiPhase.Deploying;

    public void CompleteDeploy() => Phase = GuiPhase.Idle;

    /// <summary>发生可恢复错误：回到空闲并记录面向用户的原因（技术细节由调用方另行展示）。</summary>
    public void Fail(string userFacingReason)
    {
        Phase = GuiPhase.Idle;
        LastErrorSummary = string.IsNullOrWhiteSpace(userFacingReason) ? "操作失败" : userFacingReason;
        LastOperationCancelled = false;
    }

    /// <summary>写入发布门禁状态。</summary>
    public void SetGate(ReleaseGateStatus status) => GateStatus = status;

    /// <summary>记录一次人工审核结果。</summary>
    public void NoteReviewed(string unitKey, ReviewOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(unitKey))
        {
            return;
        }

        _reviewedUnitKeys.Add(unitKey);
        _reviewOutcomes[unitKey] = outcome;
    }

    /// <summary>刷新待审核数（例如批量确认后由调用方重新写入）。</summary>
    public void SetNeedReviewCount(int needReviewCount) => NeedReviewCount = Math.Max(0, needReviewCount);

    /// <summary>重置为初始状态。</summary>
    public void Reset()
    {
        Phase = GuiPhase.Idle;
        HasAnalysis = false;
        HasTranslated = false;
        LastErrorSummary = null;
        LastOperationCancelled = false;
        TotalCount = InheritCount = NewCount = ModifiedCount = MissingTranslationCount = DeletedCount = 0;
        NeedTranslateCount = NeedReviewCount = BlockingIssueCount = 0;
        HasCanonicalMissing = false;
        GateStatus = null;
        _reviewedUnitKeys.Clear();
        _reviewOutcomes.Clear();
    }

    // ---------- 展示文案 ----------

    /// <summary>当前阶段（人话）。</summary>
    public string PhaseText => Phase switch
    {
        GuiPhase.Analyzing => "正在分析更新",
        GuiPhase.Translating => "正在翻译",
        GuiPhase.GeneratingOutput => "正在生成汉化",
        GuiPhase.Deploying => "正在部署到游戏",
        GuiPhase.Cancelling => "正在取消…",
        _ => "空闲",
    };

    /// <summary>空状态引导（首次打开时显示，避免一大片空表）。</summary>
    public string FirstRunGuideText =>
        "第一次使用请按顺序完成四步：\n"
        + "1. 选择游戏目录（或点「自动定位游戏」）\n"
        + "2. 在「配置」里填写 DeepSeek API Key 并测试连接\n"
        + "3. 选择翻译模式（推荐：韩文原文 + 英文参考）\n"
        + "4. 点击「分析更新」，然后按提示翻译、审核、生成汉化";

    /// <summary>状态栏文本（模式 / API / 分析 / 翻译 / 待审核 / 输出）。</summary>
    public string StatusBarText(string modeShortName, string apiStatus)
        => $"模式：{modeShortName}｜API：{apiStatus}｜分析：{(HasAnalysis ? "已完成" : "未开始")}"
           + $"｜待翻译：{NeedTranslateCount}｜待审核：{NeedReviewCount}｜输出：{(HasTranslated ? "已生成候选译文" : "未生成")}";
}

/// <summary>部署确认 / 结果文案（第9.0C轮：部署前必须展示目标、文件数、备份与回滚策略）。</summary>
public static class DeployPresentation
{
    /// <summary>部署确认对话框正文（必须包含目标目录、文件数、备份与回滚说明）。</summary>
    public static string BuildConfirmationText(
        string targetDirectory,
        int fileCount,
        bool requiresConfirmation,
        ReleaseGateStatus? gateStatus)
    {
        var lines = new List<string>
        {
            "即将把汉化文件部署到游戏目录：",
            string.Empty,
            $"目标目录：{targetDirectory}",
            $"即将复制的文件数：{fileCount}",
            "部署前会自动备份现有汉化文件。",
            "如果部署过程中失败，会尝试回滚到部署前的状态。",
        };

        if (gateStatus is { } status)
        {
            lines.Add(string.Empty);
            lines.Add($"当前发布门禁：{WorkflowText.GateStatus(status)}");
        }

        if (requiresConfirmation)
        {
            lines.Add("门禁要求人工确认：请确认已完成审核。");
        }

        lines.Add(string.Empty);
        lines.Add("确认要继续部署吗？");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>部署结果文案（成功给备份位置与文件数；失败给是否回滚与原因）。</summary>
    public static (string Summary, bool IsCritical) BuildOutcome(DeployResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Status == DeploymentStatus.Succeeded)
        {
            return (
                $"部署完成。{Environment.NewLine}"
                + $"复制文件：{result.DeployedCount} 个{Environment.NewLine}"
                + $"备份位置：{result.BackupDir}{Environment.NewLine}"
                + $"目标目录：{result.TargetDir}",
                false);
        }

        var lines = new List<string>
        {
            WorkflowText.DeploymentStatus(result.Status),
            $"已回滚文件数：{result.RolledBackCount}",
            $"备份位置：{result.BackupDir}",
        };

        if (result.RollbackSucceeded)
        {
            lines.Add("游戏目录已恢复到部署前的状态。");
        }
        else
        {
            lines.Add("回滚未全部完成，请按下面的提示人工检查：");
            foreach (var path in result.UnrecoveredRelativePaths.Take(5))
            {
                lines.Add($"  · {path}");
            }
        }

        var reason = result.Errors.FirstOrDefault() ?? result.RollbackErrors.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(reason))
        {
            lines.Add($"失败原因：{reason}");
        }

        return (string.Join(Environment.NewLine, lines), !result.RollbackSucceeded);
    }
}
