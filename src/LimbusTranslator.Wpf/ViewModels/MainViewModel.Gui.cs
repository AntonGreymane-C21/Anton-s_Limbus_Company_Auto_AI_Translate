using System.IO;
using System.Windows;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// MainViewModel 的「GUI 产品化」部分（第9.0C轮）。
///
/// 职责边界（不复制后端逻辑）：
///   - 模式选择：读 <see cref="AppSettingsLoader.TryLoadTranslationMode"/>、写 <see cref="AppSettingsLoader.SaveTranslationMode"/>；
///   - 状态/按钮可用性：全部来自 <see cref="GuiWorkflowState"/>（唯一状态机）；
///   - 审核展示：由 <see cref="ReviewDisplayModel.Build"/> 从后端条目 + 捕获三语文本 + 邻句上下文生成；
///   - 门禁/部署文案：由 <see cref="WorkflowText"/> / <see cref="DeployPresentation"/> 映射。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly GuiWorkflowState _workflow = new();
    private TranslationModeOption? _selectedModeOption;
    private ReviewDisplayModel? _reviewDisplay;
    private bool _modeLoaded;

    // ---------------- 翻译模式 ----------------

    /// <summary>四种模式的可选文案（GUI 只能从这里选择）。</summary>
    public IReadOnlyList<TranslationModeOption> ModeOptions => TranslationModePresentation.All;

    /// <summary>当前模式（TwoWay；切换即写入 config/appsettings.json）。</summary>
    public TranslationModeOption SelectedModeOption
    {
        get
        {
            if (!_modeLoaded)
            {
                _modeLoaded = true;
                _selectedModeOption = LoadSavedModeOption();
            }

            return _selectedModeOption ?? TranslationModePresentation.All[0];
        }
        set
        {
            if (value is null || value.Mode == SelectedModeOption.Mode)
            {
                return;
            }

            if (!_workflow.CanChangeTranslationMode)
            {
                Log("[调试] 翻译运行中，已拒绝切换翻译模式。");
                OnPropertyChanged();
                return;
            }

            _selectedModeOption = value;
            OnPropertyChanged();
            NotifyModeBindings();

            try
            {
                var configDir = Path.Combine(FindProjectRoot(), "config");
                AppSettingsLoader.SaveTranslationMode(configDir, value.Mode);
                Log($"[调试] 已保存翻译模式：{value.DisplayName}（{value.InternalCode}）");
            }
            catch (Exception ex)
            {
                Log($"[错误] 保存翻译模式失败：{ex.Message}");
                FailGui("翻译模式保存失败，请检查 config 目录是否可写。", ex);
            }
        }
    }

    /// <summary>当前模式（内部枚举；供业务判断，不直接展示给普通用户）。</summary>
    public TranslationMode CurrentTranslationMode => SelectedModeOption.Mode;

    public bool CanChangeMode => _workflow.CanChangeTranslationMode;

    public string ModeDescriptionText => SelectedModeOption.Description;

    public string ModeSubtitleText => SelectedModeOption.Subtitle;

    public string ModeLockHintText => CanChangeMode
        ? "翻译进行中会锁定模式，结束后可再次修改。"
        : "运行中：翻译模式已锁定，本次运行结束后可修改。";

    // ---------------- 状态栏 / 首次引导 ----------------

    /// <summary>API 配置状态（只读展示；详细配置在「配置」窗口）。</summary>
    public string ApiStatusText { get; private set; } = "未检测";

    /// <summary>底部状态栏文本。</summary>
    public string StatusBarText => _workflow.StatusBarText(
        TranslationModePresentation.ShortName(CurrentTranslationMode), ApiStatusText);

    public string CurrentPhaseText => _workflow.PhaseText;

    public bool ShowFirstRunGuide => _workflow.ShowFirstRunGuide;

    public string FirstRunGuideText => _workflow.FirstRunGuideText;

    public Visibility FirstRunGuideVisibility => ShowFirstRunGuide ? Visibility.Visible : Visibility.Collapsed;

    // ---------------- 按钮可用性（唯一来源：GuiWorkflowState） ----------------

    public bool CanAnalyzeAction => _workflow.CanAnalyze;

    public bool CanTranslateAction => _workflow.CanTranslate && HasSelectedFiles;

    public bool CanGenerateOutputAction => _workflow.CanGenerateOutput;

    public bool CanDeployAction => _workflow.CanDeploy;

    public bool CanSyncGlossaryAction => _workflow.CanSyncGlossary;

    public bool RequiresDeployConfirmation => _workflow.RequiresDeployConfirmation;

    /// <summary>没有需要 AI 翻译的文本时的提示（不启动 Provider）。</summary>
    public string NoTranslateHintText => _workflow is { HasAnalysis: true, NeedTranslateCount: 0 }
        ? "没有需要调用 AI 翻译的文本。"
        : string.Empty;

    public Visibility NoTranslateHintVisibility
        => string.IsNullOrEmpty(NoTranslateHintText) ? Visibility.Collapsed : Visibility.Visible;

    // ---------------- 分析结果 Dashboard（人话） ----------------

    public string AnalysisDashboardText
    {
        get
        {
            if (!_workflow.HasAnalysis)
            {
                return "尚未分析。点击「分析更新」后这里会显示本次更新统计。";
            }

            var lines = new List<string>
            {
                $"总文本：{_workflow.TotalCount}｜无需修改：{_workflow.InheritCount}｜继承旧翻译：{_workflow.InheritCount}",
                $"新增：{_workflow.NewCount}｜已修改：{_workflow.ModifiedCount}｜缺少翻译：{_workflow.MissingTranslationCount}｜原文已删除：{_workflow.DeletedCount}",
                $"需要 AI 翻译：{_workflow.NeedTranslateCount}｜需要人工确认：{_workflow.NeedReviewCount}｜阻塞问题：{_workflow.BlockingIssueCount}",
            };

            if (CurrentTranslationMode != TranslationMode.EnglishOnly)
            {
                var fallback = CountFallbackEntries();
                lines.Add(
                    $"韩文原文缺失：{Entries.Count(entry => entry.CanonicalKoreanText is null)}"
                    + $"｜参考译本缺失（回退韩文）：{fallback}"
                    + $"｜使用韩文回退：{fallback}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }

    // ---------------- 审核页：四模式语言展示 ----------------

    public string ReviewModeDisplayName => _reviewDisplay?.ModeDisplayName ?? "（未选中条目）";

    public string ReviewPrimaryLabel => _reviewDisplay?.PrimarySource.Label ?? "权威原文";

    public string ReviewPrimaryText => _reviewDisplay?.PrimarySource.DisplayText ?? string.Empty;

    public string ReviewPrimaryNote => _reviewDisplay?.PrimarySource.Note ?? string.Empty;

    public string ReviewReferenceLabel => _reviewDisplay?.ReferenceSource?.Label ?? "参考译文";

    public string ReviewReferenceText => _reviewDisplay?.ReferenceSource?.DisplayText ?? "（该模式不使用参考译文）";

    public string ReviewReferenceNote => _reviewDisplay is null
        ? string.Empty
        : _reviewDisplay.ReferenceSource is null
            ? _reviewDisplay.ReferencePanelNote
            : $"{_reviewDisplay.ReferenceSource.Note}　{_reviewDisplay.ReferencePanelNote}";

    public Visibility ReviewReferenceVisibility
        => _reviewDisplay is null || (_reviewDisplay.ReferenceSource is null && string.IsNullOrEmpty(_reviewDisplay.ReferencePanelNote))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public IReadOnlyList<ReviewTextBlock> ReviewOtherLanguages
        => _reviewDisplay?.OtherLanguages ?? Array.Empty<ReviewTextBlock>();

    public string ReviewAiReviewReasonText => _reviewDisplay?.AiReviewReason ?? string.Empty;

    public Visibility ReviewAiReviewVisibility
        => string.IsNullOrEmpty(_reviewDisplay?.AiReviewReason) ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<ReviewIssueRow> ReviewLanguageIssues
        => _reviewDisplay is null ? Array.Empty<ReviewIssueRow>() : (IReadOnlyList<ReviewIssueRow>)_reviewDisplay.Issues;

    public string ReviewIssuePanelNote => _reviewDisplay?.IssuePanelNote ?? "未发现校验问题";

    public IReadOnlyList<ReviewTermRow> ReviewTermRows
        => _reviewDisplay?.Terms ?? Array.Empty<ReviewTermRow>();

    public string ReviewTermPanelNote => _reviewDisplay?.TermPanelNote ?? "未命中术语";

    public string ReviewNeighborLanguageLabel => _reviewDisplay?.NeighborLanguageLabel ?? "上下文语言：—";

    public string ReviewNeighborNote => _reviewDisplay?.NeighborPanelNote ?? string.Empty;

    public string ReviewNeighborPreviousText => _reviewDisplay?.PreviousText ?? "（无）";

    public string ReviewNeighborNextText => _reviewDisplay?.NextText ?? "（无）";

    public string ReviewEffectiveLanguageText => _reviewDisplay?.EffectiveLanguageText ?? "—";

    public string ReviewStateDisplayText => _reviewDisplay?.ReviewStateText ?? "—";

    public IReadOnlyList<string> ReviewTechnicalRowsTexts
        => _reviewDisplay?.TechnicalRows ?? Array.Empty<string>();

    public string ReviewProgressText => _workflow.ReviewProgressText;

    // ---------------- 审核页：搜索 ----------------

    private string _reviewSearchText = string.Empty;

    /// <summary>搜索关键词（中文 / 英文 / 韩文 / 日文 / UnitKey / 文件名 / Speaker）。</summary>
    public string ReviewSearchText
    {
        get => _reviewSearchText;
        set
        {
            if (value == _reviewSearchText)
            {
                return;
            }

            _reviewSearchText = value ?? string.Empty;
            OnPropertyChanged();
            ApplyReviewFilter();
        }
    }

    // ---------------- 发布门禁（人话 + 可定位的问题列表） ----------------

    /// <summary>门禁状态（中文）。</summary>
    public string GateStatusDisplayText { get; private set; } = "尚未检查";

    /// <summary>门禁状态的下一步提示。</summary>
    public string GateNextStepText { get; private set; } = "翻译并审核后会进行发布检查。";

    /// <summary>门禁问题列表（文件 / UnitKey / 类型 / 说明 / 是否阻塞）。</summary>
    public IReadOnlyList<GateIssueRow> GateIssueRows => BuildGateIssueRows();

    private IReadOnlyList<GateIssueRow> BuildGateIssueRows()
    {
        var rows = new List<GateIssueRow>();
        foreach (var entry in ReviewEntries)
        {
            foreach (var issue in entry.ValidationIssues)
            {
                var (name, explanation) = WorkflowText.Issue(issue.Code);
                rows.Add(new GateIssueRow(
                    entry.Key.RelativeFilePath,
                    entry.Key.ToString(),
                    $"{WorkflowText.Severity(issue.Severity)}｜{name}",
                    explanation,
                    WorkflowText.IsBlocking(issue.Severity)));
            }
        }

        return rows;
    }

    // ---------------- 部署确认 / 结果 ----------------

    /// <summary>生成部署确认对话框正文（目标目录 / 文件数 / 备份 / 回滚）。</summary>
    /// <param name="targetDirectoryOverride">目标目录（null ⇒ 用自动定位的游戏汉化目录推算）</param>
    /// <param name="targetLabel">目标标签（游戏目录 / 汉化文件夹）</param>
    /// <param name="restrictToRelativePaths">
    /// 第9.0C.13轮：增量部署范围（非 null ⇒ 文案按"只部署这些文件"生成并写明范围）。
    /// </param>
    public string BuildDeployConfirmationText(
        string? targetDirectoryOverride = null,
        string targetLabel = "游戏目录",
        IReadOnlyCollection<string>? restrictToRelativePaths = null)
    {
        var target = targetDirectoryOverride;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = string.IsNullOrWhiteSpace(GameRootDir)
                ? "（未定位游戏目录，请先点「自动定位游戏」）"
                : Path.Combine(GameRootDir, "LimbusCompany_Data", "Lang", "LLC_zh-CN");
        }

        var fileCount = restrictToRelativePaths is null
            ? CountOutputFiles()
            : restrictToRelativePaths.Count;

        var text = DeployPresentation.BuildConfirmationText(
            target, fileCount, RequiresDeployConfirmation, _workflow.GateStatus, targetLabel);

        if (restrictToRelativePaths is not null)
        {
            text = $"【增量部署】本次**只部署本轮「任务范围」中勾选的文件**（{fileCount} 个），"
                   + "目标目录里的其他文件不会被触碰。\n\n" + text;
        }

        return text;
    }

    /// <summary>
    /// 第9.0C.9轮：最近一次选择过的「部署到汉化文件夹」目标目录。
    /// 仅本次会话内记忆（用于预填文件夹选择框），不写入配置文件。
    /// </summary>
    public string LastDeployFolderPath { get; set; } = string.Empty;

    private int CountOutputFiles()
    {
        try
        {
            var outputDir = Path.Combine(FindProjectRoot(), "data", "output");
            if (!Directory.Exists(outputDir))
            {
                return 0;
            }

            // 第9.0C.21轮（R6）：部署确认里的"即将复制的文件数"必须等于**清单里的文件数**
            //（部署本身也只按清单复制）。旧实现递归数 output 下全部 *.json，
            // 会把非权威产物（如 real_api_smoke/）一并算进去 ⇒ 数字虚高。
            var manifest = OutputManifestService.Load(outputDir);
            if (manifest is not null && manifest.Files.Count > 0)
            {
                return manifest.Files.Count;
            }

            return Directory.EnumerateFiles(outputDir, "*.json", SearchOption.AllDirectories)
                .Count(path => !OutputWorkspaceHygiene.IsNonAuthoritative(Path.GetRelativePath(outputDir, path)));
        }
        catch
        {
            return 0;
        }
    }

    // ---------------- 错误提示（用户可读 + 技术详情） ----------------

    /// <summary>面向用户的失败摘要。</summary>
    public string LastErrorSummary { get; private set; } = string.Empty;

    /// <summary>技术详情（可展开）。</summary>
    public string LastErrorDetail { get; private set; } = string.Empty;

    /// <summary>需要弹出错误对话框时触发（由窗口代码显示）。</summary>
    public event Action<string, string>? ErrorDialogRequested;

    internal void FailGui(string userFacingReason, Exception? exception = null)
    {
        _workflow.Fail(userFacingReason);
        LastErrorSummary = userFacingReason;
        LastErrorDetail = exception is null ? string.Empty : exception.ToString();
        Log($"[错误] {userFacingReason}" + (exception is null ? string.Empty : $"（{exception.GetType().Name}: {exception.Message}）"));
        NotifyWorkflowBindings();
        ErrorDialogRequested?.Invoke(userFacingReason, LastErrorDetail);
    }

    // ---------------- 工作流钩子（由既有流程调用；计数全部来自后端结果） ----------------

    internal void BeginGuiAnalyze()
    {
        _workflow.BeginAnalyze();
        NotifyWorkflowBindings();
    }

    /// <summary>
    /// 第9.0C.1轮：用**完整后端结果**写入 GUI 状态（界面只保留预览条目，
    /// 因此统计绝不能从 <c>Entries</c> 预览集合推算）。
    /// </summary>
    internal void CompleteGuiAnalyzeFrom(ProductionAnalyzeResult result)
    {
        var diff = result.DiffResult;
        var plan = result.Plan;
        var blocking = diff.Entries.Sum(entry => entry.ValidationIssues.Count(issue => issue.Severity == ValidationSeverity.Error));
        var needReview = diff.Entries.Count(entry => entry.NeedsReview);
        var canonicalMissing = CurrentTranslationMode != TranslationMode.EnglishOnly
                               && diff.Entries.Any(entry => entry.CanonicalKoreanText is null);

        _workflow.CompleteAnalyze(
            totalCount: diff.Entries.Count,
            inheritCount: diff.Entries.Count(entry => entry.Action == TranslationAction.Inherit),
            newCount: diff.AddedCount,
            modifiedCount: diff.ModifiedCount,
            missingTranslationCount: diff.MissingTranslationCount,
            deletedCount: diff.Entries.Count(entry => entry.Action == TranslationAction.SkipDeleted),
            needTranslateCount: plan.NeedTranslate.Count,
            needReviewCount: needReview,
            blockingIssueCount: blocking,
            hasCanonicalMissing: canonicalMissing);
        NotifyWorkflowBindings();
    }


    internal void BeginGuiTranslate()
    {
        _workflow.BeginTranslate();
        NotifyWorkflowBindings();
    }

    internal void CompleteGuiTranslate()
    {
        _workflow.CompleteTranslate();
        _workflow.SetNeedReviewCount(ReviewEntries.Count);
        NotifyWorkflowBindings();
    }

    internal void BeginGuiGenerateOutput()
    {
        _workflow.BeginGenerateOutput();
        NotifyWorkflowBindings();
    }

    internal void CompleteGuiGenerateOutput()
    {
        _workflow.CompleteGenerateOutput();
        NotifyWorkflowBindings();
    }

    internal void BeginGuiDeploy()
    {
        _workflow.BeginDeploy();
        NotifyWorkflowBindings();
    }

    internal void CompleteGuiDeploy()
    {
        _workflow.CompleteDeploy();
        NotifyWorkflowBindings();
    }

    /// <summary>记录一次人工审核结果（用于「已接受 / 已修改」进度）。</summary>
    internal void NoteReviewOutcome(bool edited)
    {
        var entry = SelectedReviewEntry;
        if (entry is null)
        {
            return;
        }

        _workflow.NoteReviewed(entry.Key.ToString(), edited ? ReviewOutcome.Edited : ReviewOutcome.Accepted);
        OnPropertyChanged(nameof(ReviewProgressText));
    }

    // ---------------- 审核展示刷新 ----------------

    internal void RefreshReviewDisplay()
    {
        var entry = SelectedReviewEntry;
        if (entry is null)
        {
            _reviewDisplay = null;
            NotifyReviewBindings();
            return;
        }

        MultilingualUnitSources? sources = null;
        _lastCapture?.Sources.TryGetValue(entry.Key.ToString(), out sources);
        var context = _reviewContextBuilder?.Build(entry);
        _reviewDisplay = ReviewDisplayModel.Build(entry, CurrentTranslationMode, sources, context, _activeGlossarySnapshot);
        NotifyReviewBindings();
    }

    /// <summary>写入门禁状态（字符串来自既有 GetStatus 流程，解析失败保持原值）。</summary>
    internal void ApplyGateStatusText(string statusText)
    {
        GateStatusDisplayText = statusText;
        GateNextStepText = "翻译并审核后会进行发布检查。";

        if (System.Enum.TryParse<LimbusTranslator.Core.Release.ReleaseGateStatus>(statusText, out var status))
        {
            _workflow.SetGate(status);
            GateStatusDisplayText = WorkflowText.GateStatus(status);
            GateNextStepText = WorkflowText.GateNextStep(status);
        }

        NotifyWorkflowBindings();
    }

    /// <summary>按 UnitKey 定位审核条目（门禁问题列表双击跳转用）。</summary>
    public bool FocusReviewEntry(string? unitKey)
    {
        if (string.IsNullOrWhiteSpace(unitKey))
        {
            return false;
        }

        var entry = ReviewEntries.FirstOrDefault(item => item.Key.ToString() == unitKey);
        if (entry is null)
        {
            return false;
        }

        SelectedReviewEntry = entry;
        RefreshReviewDisplay();
        Log($"[调试] 已定位到审核条目：{unitKey}");
        return true;
    }

    // ---------------- 私有辅助 ----------------

    private TranslationModeOption LoadSavedModeOption()
    {
        try
        {
            var configDir = Path.Combine(FindProjectRoot(), "config");
            if (AppSettingsLoader.TryLoadTranslationMode(configDir, out var mode, out var error))
            {
                return TranslationModePresentation.Resolve(mode);
            }

            Log($"[错误] 读取翻译模式失败：{error}（已回退英文翻译）");
        }
        catch (Exception ex)
        {
            Log($"[错误] 读取翻译模式异常：{ex.Message}");
        }

        return TranslationModePresentation.All[0];
    }

    private int CountFallbackEntries()
        => Entries.Count(entry =>
            entry.RunTranslationMode is TranslationMode.KoreanEnglish or TranslationMode.KoreanJapanese
            && entry.EffectiveSourceLanguage == SourceLanguage.Korean);

    private void NotifyModeBindings()
    {
        foreach (var name in new[]
                 {
                     nameof(SelectedModeOption), nameof(CurrentTranslationMode), nameof(ModeDescriptionText),
                     nameof(ModeSubtitleText), nameof(ModeLockHintText), nameof(CanChangeMode),
                 })
        {
            OnPropertyChanged(name);
        }

        NotifyWorkflowBindings();
    }

    private void NotifyWorkflowBindings()
    {
        foreach (var name in new[]
                 {
                     nameof(StatusBarText), nameof(CurrentPhaseText), nameof(ShowFirstRunGuide),
                     nameof(FirstRunGuideText), nameof(FirstRunGuideVisibility),
                     nameof(CanAnalyzeAction), nameof(CanTranslateAction), nameof(CanGenerateOutputAction),
                     nameof(CanDeployAction), nameof(CanSyncGlossaryAction), nameof(RequiresDeployConfirmation),
                     nameof(CanChangeMode), nameof(ModeLockHintText),
                     nameof(NoTranslateHintText), nameof(NoTranslateHintVisibility),
                     nameof(AnalysisDashboardText), nameof(ReviewProgressText),
                     nameof(GateIssueRows), nameof(GateStatusDisplayText), nameof(GateNextStepText),
                     nameof(LastErrorSummary), nameof(LastErrorDetail),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    private void NotifyReviewBindings()
    {
        foreach (var name in new[]
                 {
                     nameof(ReviewModeDisplayName), nameof(ReviewPrimaryLabel), nameof(ReviewPrimaryText),
                     nameof(ReviewPrimaryNote), nameof(ReviewReferenceLabel), nameof(ReviewReferenceText),
                     nameof(ReviewReferenceNote), nameof(ReviewReferenceVisibility), nameof(ReviewOtherLanguages),
                     nameof(ReviewAiReviewReasonText), nameof(ReviewAiReviewVisibility),
                     nameof(ReviewLanguageIssues), nameof(ReviewIssuePanelNote),
                     nameof(ReviewTermRows), nameof(ReviewTermPanelNote),
                     nameof(ReviewNeighborLanguageLabel), nameof(ReviewNeighborNote),
                     nameof(ReviewNeighborPreviousText), nameof(ReviewNeighborNextText),
                     nameof(ReviewEffectiveLanguageText), nameof(ReviewStateDisplayText),
                     nameof(ReviewTechnicalRowsTexts),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ---------------- 第9.0C.1轮：后台执行 / 取消 / 节流进度 ----------------

    /// <summary>界面预览上限（后端结果仍完整保留；只构造界面真正需要的条目）。</summary>
    internal const int UiPreviewEntryCount = 200;

    internal const int UiPreviewFileCount = 300;

    private readonly object _operationGate = new();
    private CancellationTokenSource? _operationCts;
    private ProductionAnalyzeService? _analyzeService;

    /// <summary>当前是否有可取消的操作（分析 / 翻译 / 生成 / 部署中；取消期间为 false）。</summary>
    public bool CanCancelOperation => _workflow.CanCancel;

    /// <summary>是否处于「正在取消」（后台尚未退出，禁止一切新操作）。</summary>
    public bool IsCancellingOperation => _workflow.IsCancelling;

    /// <summary>取消按钮文案。</summary>
    public string CancelButtonText => _workflow.IsCancelling ? "正在取消…" : "取消当前操作";

    /// <summary>取消期间的提示文案（非取消期间为空）。</summary>
    public string CancellingHintText => _workflow.IsCancelling
        ? "正在取消：等待后台安全退出（不会写入半成品输出，也不会部署）。"
        : string.Empty;

    public Visibility CancellingHintVisibility
        => _workflow.IsCancelling ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>等待后台任务退出（不阻塞消息泵：Task.Delay 会回到 UI 线程继续处理消息）。</summary>
    internal async Task<bool> WaitForIdleAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsBusy && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        return !IsBusy;
    }

    /// <summary>为一次可取消操作创建令牌（同一时刻只允许一个操作）。</summary>
    internal CancellationToken BeginCancellableOperation()
    {
        lock (_operationGate)
        {
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            return _operationCts.Token;
        }
    }

    internal void EndCancellableOperation()
    {
        lock (_operationGate)
        {
            _operationCts?.Dispose();
            _operationCts = null;
        }
    }

    /// <summary>用户点击「取消当前操作」。</summary>
    public void CancelCurrentOperation()
    {
        CancellationTokenSource? cts;
        lock (_operationGate)
        {
            cts = _operationCts;
        }

        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        _workflow.BeginCancel();
        StatusText = "正在取消…";
        Log("[调试] 已请求取消当前操作（等待安全退出；不会写入半成品部署）。");
        NotifyWorkflowBindings();

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作已结束并释放：忽略
        }
    }

    internal ProductionAnalyzeService GetAnalyzeService(string configDir)
        => _analyzeService ??= new ProductionAnalyzeService(configDir);

    /// <summary>
    /// 应用后台分析进度（由 <see cref="IProgress{T}"/> 回到 UI 线程后调用；后台侧已按 150ms 节流）。
    /// </summary>
    internal void ApplyAnalyzeProgress(AnalyzeStageReport report)
    {
        if (report.HasKnownTotal)
        {
            UpdateProgress(report.Done, report.Total, report.Stage);
        }
        else
        {
            SetProgressStage(report.Stage);
        }
    }

    /// <summary>
    /// 把后台分析结果映射到界面（**只在 UI 线程执行**；只构造界面需要的预览条目）。
    /// 统计全部来自后端结果，不重新计算 Diff。
    /// </summary>
    internal void ApplyAnalyzeResult(ProductionAnalyzeResult result)
    {
        // 第9.0C.3轮：把任务文件列表构建收口在同一个 partial 里（内存过滤，不重新扫描游戏目录）。
        RefreshFileTasksFromAnalysis(result);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var fileResult = result.FileResult;

        FileNewCount = fileResult.NewCount;
        FileMissingCount = fileResult.MissingChineseCount;
        FileModifiedCount = fileResult.ModifiedCount;
        FileUnchangedCount = fileResult.UnchangedCount;
        FileDeletedCount = fileResult.DeletedCount;
        _categorizedFileEntries = fileResult.Entries.ToList();

        FileEntries.Clear();
        foreach (var file in _categorizedFileEntries
                     .Where(entry => entry.Kind != FileDiffKind.Unchanged)
                     .Take(UiPreviewFileCount))
        {
            FileEntries.Add(file);
        }

        BuildCategoryStats();

        var diff = result.DiffResult;
        AddedCount = diff.AddedCount;
        ModifiedCount = diff.ModifiedCount;
        UnchangedCount = diff.UnchangedCount;
        DeletedCount = diff.DeletedCount;
        MissingCount = diff.MissingTranslationCount;
        TotalCount = diff.Entries.Count;

        Entries.Clear();
        foreach (var entry in diff.Entries.Take(UiPreviewEntryCount))
        {
            Entries.Add(entry);
        }

        _lastCapture = result.Capture;

        foreach (var timing in result.Timings)
        {
            Log(timing.Describe());
        }

        Log($"[调试] 快照根：{result.SnapshotRoot}"
            + (SnapshotRootResolver.IsIsolated(FindProjectRoot(), result.SnapshotRoot) ? "（已隔离：不写生产快照）" : "（生产路径）"));
        Log($"[调试] 旧中文范围：{result.OldChineseScope.Describe()}");
        Log($"[调试] 生产计划：待翻译 {result.Plan.NeedTranslate.Count} 条");

        CompleteGuiAnalyzeFrom(result);
        watch.Stop();
        Log($"[调试] GUI结果映射完成：耗时 {watch.ElapsedMilliseconds}ms"
            + $"（预览 {Entries.Count}/{diff.Entries.Count} 条，文件 {FileEntries.Count}/{_categorizedFileEntries.Count} 个）");
    }
}

/// <summary>门禁问题行（文件 / UnitKey / 类型 / 说明 / 是否阻塞）。</summary>
public sealed record GateIssueRow(string FilePath, string UnitKey, string Kind, string Explanation, bool IsBlocking)
{
    public string Headline => (IsBlocking ? "阻塞｜" : "需确认｜") + Kind;
}
