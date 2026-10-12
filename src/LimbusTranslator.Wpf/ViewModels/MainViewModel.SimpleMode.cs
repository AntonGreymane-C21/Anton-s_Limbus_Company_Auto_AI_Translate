using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Review;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>简易模式步骤条上的一个步骤。</summary>
/// <param name="Number">序号（1~4）</param>
/// <param name="Title">标题</param>
/// <param name="State">状态文本：Pending / Active / Done（供 XAML DataTrigger 使用）</param>
/// <param name="IsFirst">是否第一个（隐藏左侧连线）</param>
/// <param name="IsLast">是否最后一个（隐藏右侧连线）</param>
public sealed record SimpleStepItem(int Number, string Title, string State, bool IsFirst, bool IsLast)
{
    /// <summary>圆点里显示的字符：完成 = ✓，其余 = 序号。</summary>
    public string Glyph => State == nameof(SimpleStepState.Done) ? "✓" : Number.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// 简易模式（新）：把原工作台收敛为「定位游戏 → 分析 → 翻译 → 部署」四步，一次只显示当前这一步。
///
/// 设计约束：
///   - **不复制任何业务逻辑**：定位 / 分析 / 翻译 / 审核保存 / 重新输出 / 部署全部调用既有方法，
///     安全语义（fail-closed 配置、发布门禁、备份回滚、HardSafety 校验）与专家模式完全相同；
///   - 简易模式只额外维护几个「本次会话走到哪一步」的标记（见 <see cref="SimpleFlowSnapshot"/>），
///     阶段判断是纯函数 <see cref="SimpleModeFlow.Resolve"/>（可单测）；
///   - 专家模式（原工作台）界面与行为保持不变；两种模式共享同一个 ViewModel，随时可以切换。
/// </summary>
public sealed partial class MainViewModel
{
    // ---------------- 界面模式（简易 / 专家；记住上次选择） ----------------

    private UiMode _uiMode = UiMode.Simple;
    private bool _simpleInitialized;

    /// <summary>当前是否为简易模式。切换时写入 data/ui_prefs.json（失败不影响使用）。</summary>
    public bool IsSimpleMode
    {
        get => _uiMode == UiMode.Simple;
        set
        {
            var mode = value ? UiMode.Simple : UiMode.Expert;
            if (mode == _uiMode)
            {
                return;
            }

            _uiMode = mode;
            if (!UiModePreferences.Save(UiModePreferences.GetPath(FindProjectRoot()), mode))
            {
                Log("[调试] 界面模式偏好保存失败（不影响使用，下次启动可能回到简易模式）。");
            }

            // 简易模式里保存过审核 ⇒ 回到专家模式时刷新一次审核列表的筛选统计
            if (mode == UiMode.Expert && _expertReviewViewStale)
            {
                _expertReviewViewStale = false;
                try
                {
                    ApplyReviewFilter();
                }
                catch (Exception ex)
                {
                    Log($"[调试] 切换到专家模式时刷新审核列表失败（不影响数据）：{ex.Message}");
                }
            }

            Log($"[调试] 界面模式：{(mode == UiMode.Simple ? "简易模式" : "专家模式")}");
            OnPropertyChanged();
            OnPropertyChanged(nameof(SimpleModeVisibility));
            OnPropertyChanged(nameof(ExpertModeVisibility));
            if (mode == UiMode.Simple)
            {
                RefreshSimpleMode();
            }
        }
    }

    public Visibility SimpleModeVisibility => IsSimpleMode ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExpertModeVisibility => IsSimpleMode ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// 窗口创建时调用一次：读取上次的界面模式（首次启动 = 简易模式），并开始跟随既有状态刷新简易界面。
    /// </summary>
    public void InitializeSimpleMode()
    {
        if (_simpleInitialized)
        {
            return;
        }

        _simpleInitialized = true;
        _uiMode = UiModePreferences.Load(UiModePreferences.GetPath(FindProjectRoot()));
        PropertyChanged += OnPropertyChangedForSimpleMode;
        RefreshSimpleMode();
    }

    // ---------------- 刷新（合并到 UI 线程，避免频繁重算） ----------------

    /// <summary>这些既有属性变化时，简易界面需要重新判断当前步骤。</summary>
    private static readonly HashSet<string> SimpleWatchedProperties = new(StringComparer.Ordinal)
    {
        nameof(IsBusy), nameof(GameRootDir), nameof(NewEnglishDir), nameof(OldChineseDir),
        nameof(StatusText), nameof(StatusBarText), nameof(CanTranslateAction),
        nameof(GateStatusText), nameof(GateStatusDisplayText), nameof(GateNextStepText), nameof(GateReasonText),
        nameof(GateNeedsReviewCount), nameof(GateWarningCount),
        nameof(IsDeployAllowed), nameof(GateConfirmationVisibility), nameof(GateConfirmationAcknowledged),
        nameof(ReviewFilterSummaryText), nameof(CanRetranslateFailed), nameof(LastDeployOutcomeText),
        nameof(ProviderStatusText), nameof(ModelStatusText), nameof(ConfigStatusText),
        nameof(LastErrorSummary), nameof(CanCancelOperation),
    };

    private readonly object _simpleRefreshGate = new();
    private bool _simpleRefreshQueued;

    private void OnPropertyChangedForSimpleMode(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && SimpleWatchedProperties.Contains(name))
        {
            ScheduleSimpleRefresh();
        }
    }

    /// <summary>
    /// 请求刷新简易界面：可从任意线程调用（例如后台的 <c>MergeAndRecordOutput</c>）；
    /// 同一轮消息循环内的多次请求合并为一次，在 UI 线程执行。
    /// </summary>
    private void ScheduleSimpleRefresh()
    {
        if (!_simpleInitialized)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            RefreshSimpleMode();
            return;
        }

        lock (_simpleRefreshGate)
        {
            if (_simpleRefreshQueued)
            {
                return;
            }

            _simpleRefreshQueued = true;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            lock (_simpleRefreshGate)
            {
                _simpleRefreshQueued = false;
            }

            RefreshSimpleMode();
        }));
    }

    private SimpleStage _simpleStage = SimpleStage.LocateGame;
    private int _simplePendingReviewCount;
    private IReadOnlyList<SimpleStepItem> _simpleSteps = Array.Empty<SimpleStepItem>();

    /// <summary>重新判断当前步骤并通知全部简易模式绑定（只在 UI 线程调用）。</summary>
    private void RefreshSimpleMode()
    {
        if (!_simpleInitialized)
        {
            return;
        }

        try
        {
            _simplePendingReviewCount = _reviewAllEntries.Count(entry => entry.NeedsReview);
            _simpleStage = SimpleModeFlow.Resolve(new SimpleFlowSnapshot(
                GameReady: IsSimpleGameReady(),
                HasAnalysis: _workflow.HasAnalysis,
                NeedTranslateCount: _workflow.NeedTranslateCount,
                OutputReady: _simpleOutputReady,
                PendingReviewCount: _simplePendingReviewCount,
                ReviewSkipped: _simpleReviewSkipped,
                ReviewChangedSinceOutput: _reviewChangedSinceOutput,
                Deployed: _simpleDeployed));

            if (_simpleStage == SimpleStage.Review)
            {
                EnsureSimpleReviewEntry();
            }

            var states = SimpleModeFlow.StepStates(_simpleStage);
            var titles = SimpleModeFlow.StepTitles;
            var steps = titles
                .Select((title, index) => new SimpleStepItem(
                    index + 1, title, states[index].ToString(), index == 0, index == titles.Count - 1))
                .ToList();
            if (!steps.SequenceEqual(_simpleSteps))
            {
                _simpleSteps = steps;
                OnPropertyChanged(nameof(SimpleSteps));
            }

            NotifySimpleBindings();
        }
        catch (Exception ex)
        {
            Log($"[调试] 简易模式界面刷新失败（不影响数据）：{ex.Message}");
        }
    }

    private void NotifySimpleBindings()
    {
        foreach (var name in new[]
                 {
                     nameof(SimpleStageName),
                     nameof(SimpleLocateVisibility), nameof(SimpleAnalyzeVisibility), nameof(SimpleTranslateVisibility),
                     nameof(SimpleUpToDateVisibility), nameof(SimpleReviewVisibility), nameof(SimpleApplyReviewVisibility),
                     nameof(SimpleDeployVisibility), nameof(SimpleDoneVisibility), nameof(SimpleBusyVisibility),
                     nameof(SimpleGameRootText), nameof(SimpleNeedTranslateText),
                     nameof(SimpleEngineText), nameof(SimpleEngineTone), nameof(SimpleEngineShortText),
                     nameof(SimpleEngineErrorVisibility), nameof(SimpleCanTranslate),
                     nameof(SimpleLastRunNote), nameof(SimpleLastRunNoteTone), nameof(SimpleLastRunNoteVisibility),
                     nameof(SimpleRetranslateVisibility), nameof(SimpleFailedEntriesText),
                     nameof(SimpleReviewRemainingText), nameof(SimpleReviewSourceLabel), nameof(SimpleReviewSourceText),
                     nameof(SimpleReviewSpeakerText), nameof(SimpleReviewSpeakerVisibility),
                     nameof(SimpleReviewIssueText), nameof(SimpleReviewIssueTone),
                     nameof(SimpleReviewNote), nameof(SimpleReviewNoteTone), nameof(SimpleReviewNoteVisibility),
                     nameof(SimpleReviewEmptyVisibility), nameof(SimpleReviewEditorVisibility),
                     nameof(SimpleReviewedCountText), nameof(SimpleSkippedReviewVisibility), nameof(SimpleSkippedReviewText),
                     nameof(SimpleGateTone), nameof(SimpleGateTitle), nameof(SimpleGateDetail),
                     nameof(SimpleGateConfirmVisibility), nameof(SimpleCanDeploy), nameof(SimpleDeployTargetText),
                     nameof(SimpleDoneText), nameof(SimpleOutputSummaryText),
                     nameof(SimpleCancelVisibility), nameof(SimpleCanCancel), nameof(SimpleCancelText),
                     nameof(SimpleCancellingHint),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ---------------- 简易模式自己的流程标记（只表示「本次会话走到哪一步」） ----------------

    private volatile bool _simpleOutputReady;
    private volatile bool _reviewChangedSinceOutput;
    private volatile bool _simpleDeployed;
    private volatile bool _simpleLastDeployFailed;
    private bool _simpleReviewSkipped;
    private bool _expertReviewViewStale;
    private string _simpleOutputSummary = string.Empty;

    /// <summary>开始新的分析 / 翻译：之前的输出、审核修改与部署结果都不再代表「当前这一轮」。</summary>
    private void ResetSimpleFlowForRun()
    {
        _simpleOutputReady = false;
        _reviewChangedSinceOutput = false;
        _simpleDeployed = false;
        _simpleLastDeployFailed = false;
        _simpleReviewSkipped = false;
        _simpleReviewedCount = 0;
        _simpleReviewEntry = null;
        _simpleReviewDisplay = null;
        _simpleReviewJustSaved = false;
        _simpleOutputSummary = string.Empty;
        ScheduleSimpleRefresh();
    }

    /// <summary>
    /// 输出已生成并写入清单（由 <c>MergeAndRecordOutput</c> 调用，**可能在后台线程**）：
    /// 审核修改已应用、需要重新部署。
    /// </summary>
    private void NoteSimpleOutputGenerated(int writtenFileCount, int requestedFileCount)
    {
        _simpleOutputReady = true;
        _reviewChangedSinceOutput = false;
        _simpleDeployed = false;
        _simpleLastDeployFailed = false;
        _simpleOutputSummary = writtenFileCount == requestedFileCount
            ? $"已生成并核验 {writtenFileCount} 个汉化文件。"
            : $"已核验 {writtenFileCount}/{requestedFileCount} 个汉化文件（输出不完整，部署前请查看发布检查）。";
        ScheduleSimpleRefresh();
    }

    /// <summary>审核保存过（任一模式）：需要先「应用修改」重新生成输出，再部署。</summary>
    private void NoteSimpleReviewChanged()
    {
        _reviewChangedSinceOutput = true;
        ScheduleSimpleRefresh();
    }

    /// <summary>部署结束（由 <see cref="ApplyDeployResult"/> 调用）。</summary>
    private void NoteSimpleDeployResult(DeploymentStatus status)
    {
        _simpleDeployed = status == DeploymentStatus.Succeeded;
        _simpleLastDeployFailed = !_simpleDeployed;
        ScheduleSimpleRefresh();
    }

    private bool IsSimpleGameReady()
        => !string.IsNullOrWhiteSpace(GameRootDir)
           && !string.IsNullOrWhiteSpace(NewEnglishDir) && Directory.Exists(NewEnglishDir)
           && !string.IsNullOrWhiteSpace(OldChineseDir) && Directory.Exists(OldChineseDir);

    // ---------------- 阶段与卡片可见性 ----------------

    /// <summary>当前阶段名（调试 / 自动化用）。</summary>
    public string SimpleStageName => _simpleStage.ToString();

    /// <summary>步骤条（四步）。</summary>
    public IReadOnlyList<SimpleStepItem> SimpleSteps => _simpleSteps;

    private Visibility StageVisibility(SimpleStage stage)
        => !IsBusy && _simpleStage == stage ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SimpleLocateVisibility => StageVisibility(SimpleStage.LocateGame);

    public Visibility SimpleAnalyzeVisibility => StageVisibility(SimpleStage.Analyze);

    public Visibility SimpleTranslateVisibility => StageVisibility(SimpleStage.Translate);

    public Visibility SimpleUpToDateVisibility => StageVisibility(SimpleStage.UpToDate);

    public Visibility SimpleReviewVisibility => StageVisibility(SimpleStage.Review);

    public Visibility SimpleApplyReviewVisibility => StageVisibility(SimpleStage.ApplyReview);

    public Visibility SimpleDeployVisibility => StageVisibility(SimpleStage.Deploy);

    public Visibility SimpleDoneVisibility => StageVisibility(SimpleStage.Done);

    /// <summary>有任务执行中：只显示进度卡片。</summary>
    public Visibility SimpleBusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>当前操作是否真的接通了取消令牌（分析 / 翻译）；定位、输出、部署等短操作不显示取消按钮。</summary>
    private bool HasCancellableOperation
    {
        get
        {
            lock (_operationGate)
            {
                return _operationCts is not null;
            }
        }
    }

    public Visibility SimpleCancelVisibility
        => IsBusy && (HasCancellableOperation || IsCancellingOperation) ? Visibility.Visible : Visibility.Collapsed;

    public bool SimpleCanCancel => CanCancelOperation && HasCancellableOperation;

    public string SimpleCancelText => IsCancellingOperation ? "正在取消…" : "取消";

    public string SimpleCancellingHint => CancellingHintText;

    // ---------------- ① 定位游戏 / ② 分析 ----------------

    public string SimpleGameRootText => string.IsNullOrWhiteSpace(GameRootDir) ? "（未找到）" : GameRootDir;

    /// <summary>自动查找游戏目录（复用既有定位逻辑）。</summary>
    public void SimpleLocateGame()
    {
        if (!IsBusy)
        {
            LocateGameDir();
        }
    }

    /// <summary>
    /// 使用用户手动选择的游戏根目录。返回 null 表示成功；否则返回给用户看的错误说明。
    /// </summary>
    public string? SimpleApplyGameRoot(string? gameRoot)
    {
        if (IsBusy)
        {
            return "当前有任务正在运行，请等待完成后再更换游戏目录。";
        }

        if (string.IsNullOrWhiteSpace(gameRoot) || !GameDirectoryLocator.IsGameRoot(gameRoot))
        {
            return "这个文件夹不像是《边狱巴士》的安装目录。\n\n请选择包含 LimbusCompany.exe 的文件夹，"
                   + "例如：…\\steamapps\\common\\Limbus Company";
        }

        var located = GameDirectoryLocator.BuildResult(gameRoot, auto: false);
        if (!Directory.Exists(located.NewEnglishDir))
        {
            return "已找到游戏，但没有找到游戏自带的英文文本目录：\n" + located.NewEnglishDir
                   + "\n\n请确认游戏已完整安装并至少启动过一次。";
        }

        if (!Directory.Exists(located.ChineseDir))
        {
            return "已找到游戏，但还没有安装中文汉化（LLC_zh-CN）：\n" + located.ChineseDir
                   + "\n\n本工具需要在已有汉化基础上做增量更新，请先安装一次汉化包，再回来更新。";
        }

        GameRootDir = located.GameRoot;
        OldEnglishDir = located.NewEnglishDir;   // 与自动定位一致：无旧英文时用当前英文作为基准
        OldChineseDir = located.ChineseDir;
        NewEnglishDir = located.NewEnglishDir;
        StatusText = "已选择游戏目录";
        Log($"[调试] 手动选择游戏目录：{located.GameRoot}");
        RefreshEnvironmentStatus();
        RefreshSimpleMode();
        return null;
    }

    /// <summary>分析游戏更新（复用既有 Diff 分析）。</summary>
    public void SimpleAnalyze()
    {
        if (!IsBusy)
        {
            RunDiff();
        }
    }

    // ---------------- ③ 翻译 ----------------

    public string SimpleNeedTranslateText => SimpleModeFlow.FormatCount(_workflow.NeedTranslateCount);

    private bool IsConfigError
        => ConfigStatusText.StartsWith("错误", StringComparison.Ordinal)
           || ConfigStatusText.StartsWith("读取失败", StringComparison.Ordinal);

    private bool IsMockProvider => ProviderStatusText.StartsWith("Mock", StringComparison.Ordinal);

    /// <summary>翻译引擎状态（一句话）。</summary>
    public string SimpleEngineText
    {
        get
        {
            if (IsConfigError)
            {
                var detail = ConfigStatusText.StartsWith("错误：", StringComparison.Ordinal)
                    ? ConfigStatusText["错误：".Length..]
                    : ConfigStatusText;
                return $"翻译设置还没填好：{detail}　请点「打开设置」填写 API Key 后再开始。";
            }

            if (IsMockProvider)
            {
                return "当前是测试模式（Mock）：不会真正调用 AI，译文只用于测试流程。";
            }

            return $"翻译引擎：{ProviderStatusText} · {ModelStatusText}";
        }
    }

    /// <summary>引擎状态色调：ok / warn / block。</summary>
    public string SimpleEngineTone => IsConfigError ? "block" : IsMockProvider ? "warn" : "ok";

    /// <summary>页脚上的简短引擎信息。</summary>
    public string SimpleEngineShortText => IsConfigError
        ? "翻译设置未完成"
        : $"{ProviderStatusText} · {ModelStatusText}";

    public Visibility SimpleEngineErrorVisibility => IsConfigError ? Visibility.Visible : Visibility.Collapsed;

    public bool SimpleCanTranslate => !IsBusy && !IsConfigError && _workflow.NeedTranslateCount > 0;

    /// <summary>
    /// 开始翻译：简易模式**总是翻译全部需要更新的文本**（有待译内容的分类全选 + 需要 AI 的文件全选），
    /// 然后走与专家模式完全相同的「开始汉化」流程。
    /// </summary>
    public void SimpleTranslate()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            SelectFullScopeForSimpleMode();
        }
        catch (Exception ex)
        {
            Log($"[调试] 简易模式选择翻译范围失败，将按当前选择翻译：{ex.Message}");
        }

        RunTranslate();
    }

    /// <summary>与分析完成后的默认选择一致：有待译内容的分类 + 全部需要 AI 的文件。</summary>
    private void SelectFullScopeForSimpleMode()
    {
        if (CategoryStats.Count == 0 || _allFileTasks.Count == 0)
        {
            return;
        }

        foreach (var stat in CategoryStats)
        {
            stat.SelectionChanged = null;
        }

        foreach (var stat in CategoryStats)
        {
            stat.IsSelected = stat.NeedTranslateFileCount > 0;
        }

        foreach (var stat in CategoryStats)
        {
            stat.SelectionChanged = OnTaskScopeChanged;
        }

        _fileSelection.SelectAll(_allFileTasks.Where(row => row.RequiresAi).Select(row => row.LogicalFile));
        ApplyTaskScopeFilter(log: false);
        Log($"[调试] 简易模式：翻译全部需要更新的文本（{CategoryStats.Count(stat => stat.IsSelected)} 个分类，"
            + $"{_allFileTasks.Count(row => row.RequiresAi)} 个文件）。");
    }

    /// <summary>
    /// 上次操作需要用户注意的结果（失败 / 取消 / 部署失败 / 输出不完整等）；正常时为空。
    /// 显示在当前步骤卡片上方，任何步骤都能看到。
    /// </summary>
    public string SimpleLastRunNote
    {
        get
        {
            if (IsBusy)
            {
                return string.Empty;
            }

            if (_simpleLastDeployFailed && !string.IsNullOrWhiteSpace(LastDeployOutcomeText))
            {
                return LastDeployOutcomeText;
            }

            if (!string.IsNullOrWhiteSpace(_workflow.LastErrorSummary))
            {
                return _workflow.LastErrorSummary!;
            }

            var status = StatusText ?? string.Empty;
            return NeedsAttention(status) ? $"上次操作：{status}" : string.Empty;
        }
    }

    /// <summary>提示色调：失败 = block，其余（取消 / 无需翻译等）= warn。</summary>
    public string SimpleLastRunNoteTone
        => _simpleLastDeployFailed || !string.IsNullOrWhiteSpace(_workflow.LastErrorSummary) ? "block" : "warn";

    public Visibility SimpleLastRunNoteVisibility
        => string.IsNullOrWhiteSpace(SimpleLastRunNote) ? Visibility.Collapsed : Visibility.Visible;

    private static bool NeedsAttention(string status)
        => new[] { "失败", "错误", "无效", "取消", "无需翻译", "请至少", "不完整", "未通过", "不存在", "没有" }
            .Any(word => status.Contains(word, StringComparison.Ordinal));

    /// <summary>有「本批翻译失败」的条目时显示「重试失败的条目」。</summary>
    public Visibility SimpleRetranslateVisibility
        => CanRetranslateFailed ? Visibility.Visible : Visibility.Collapsed;

    public string SimpleFailedEntriesText
    {
        get
        {
            var failed = _reviewAllEntries.Count(entry => entry.ProviderBatchFailed);
            return failed > 0
                ? $"有 {SimpleModeFlow.FormatCount(failed)} 条因网络或接口问题没有翻译成功，可以只重试这些条目。"
                : "有部分条目因网络或接口问题没有翻译成功，可以只重试这些条目。";
        }
    }

    /// <summary>只重试「本批翻译失败」的条目（复用既有重译流程，会弹出确认）。</summary>
    public void SimpleRetranslateFailed()
    {
        if (!IsBusy)
        {
            RetranslateFailedEntries();
        }
    }

    // ---------------- ③ 审核（简化卡片：原文 + 可编辑译文 + 一句话问题） ----------------

    private DiffEntry? _simpleReviewEntry;
    private ReviewDisplayModel? _simpleReviewDisplay;
    private string _simpleReviewDraft = string.Empty;
    private string _simpleReviewOriginal = string.Empty;
    private string _simpleReviewNote = string.Empty;
    private string _simpleReviewNoteTone = "ok";
    private bool _simpleReviewJustSaved;
    private int _simpleReviewedCount;

    /// <summary>审核卡片当前条目（保存后若无新问题会停留在原条目上显示「已保存」，直到用户点下一条）。</summary>
    private void EnsureSimpleReviewEntry()
    {
        var current = _simpleReviewEntry;
        if (current is not null
            && _reviewAllEntries.Contains(current)
            && (current.NeedsReview || _simpleReviewJustSaved))
        {
            return;
        }

        var index = SimpleModeFlow.NextPendingIndex(_reviewAllEntries, -1, entry => entry.NeedsReview);
        ShowSimpleReviewEntry(index >= 0 ? _reviewAllEntries[index] : null);
    }

    private void ShowSimpleReviewEntry(DiffEntry? entry)
    {
        _simpleReviewEntry = entry;
        _simpleReviewJustSaved = false;
        _simpleReviewDraft = entry?.Translation ?? string.Empty;
        _simpleReviewOriginal = _simpleReviewDraft;
        _simpleReviewNote = string.Empty;
        _simpleReviewDisplay = entry is null ? null : BuildSimpleReviewDisplay(entry);
        OnPropertyChanged(nameof(SimpleReviewTranslation));
    }

    private ReviewDisplayModel? BuildSimpleReviewDisplay(DiffEntry entry)
    {
        try
        {
            MultilingualUnitSources? sources = null;
            _lastCapture?.Sources.TryGetValue(entry.Key.ToString(), out sources);
            var context = _reviewContextBuilder?.Build(entry);
            return ReviewDisplayModel.Build(entry, CurrentTranslationMode, sources, context, _activeGlossarySnapshot);
        }
        catch (Exception ex)
        {
            Log($"[调试] 审核卡片构建失败（回退为基础显示）：{ex.Message}");
            return null;
        }
    }

    public string SimpleReviewRemainingText => SimpleModeFlow.FormatCount(_simplePendingReviewCount);

    public string SimpleReviewedCountText => _simpleReviewedCount > 0
        ? $"本次已确认 {SimpleModeFlow.FormatCount(_simpleReviewedCount)} 条"
        : string.Empty;

    public string SimpleReviewSourceLabel => _simpleReviewDisplay?.PrimarySource.Label ?? "原文";

    public string SimpleReviewSourceText
        => _simpleReviewDisplay?.PrimarySource.DisplayText
           ?? _simpleReviewEntry?.NewSourceText
           ?? string.Empty;

    public string SimpleReviewSpeakerText => string.IsNullOrWhiteSpace(_simpleReviewEntry?.Speaker)
        ? string.Empty
        : $"说话人：{_simpleReviewEntry!.Speaker}";

    public Visibility SimpleReviewSpeakerVisibility
        => string.IsNullOrWhiteSpace(SimpleReviewSpeakerText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>一句话问题说明。</summary>
    public string SimpleReviewIssueText
    {
        get
        {
            var entry = _simpleReviewEntry;
            if (entry is null)
            {
                return string.Empty;
            }

            if (!entry.NeedsReview && !ReviewFilter.HasHardSafetyError(entry))
            {
                return "这条已确认，没有需要处理的问题。";
            }

            return SimpleModeFlow.IssueLine(
                _simpleReviewDisplay?.Issues,
                _simpleReviewDisplay?.AiReviewReason ?? entry.ReviewReason);
        }
    }

    /// <summary>问题色调：有结构错误 = block，已确认 = ok，其余 = warn。</summary>
    public string SimpleReviewIssueTone
    {
        get
        {
            var entry = _simpleReviewEntry;
            if (entry is null)
            {
                return "warn";
            }

            if (ReviewFilter.HasHardSafetyError(entry))
            {
                return "block";
            }

            return entry.NeedsReview ? "warn" : "ok";
        }
    }

    /// <summary>可编辑的译文草稿（保存时才写回条目）。</summary>
    public string SimpleReviewTranslation
    {
        get => _simpleReviewDraft;
        set
        {
            if (string.Equals(_simpleReviewDraft, value, StringComparison.Ordinal))
            {
                return;
            }

            _simpleReviewDraft = value ?? string.Empty;
            OnPropertyChanged();
        }
    }

    public string SimpleReviewNote => _simpleReviewNote;

    public string SimpleReviewNoteTone => _simpleReviewNoteTone;

    public Visibility SimpleReviewNoteVisibility
        => string.IsNullOrWhiteSpace(_simpleReviewNote) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SimpleReviewEditorVisibility
        => _simpleReviewEntry is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility SimpleReviewEmptyVisibility
        => _simpleReviewEntry is null ? Visibility.Visible : Visibility.Collapsed;

    private void SetSimpleReviewNote(string note, string tone)
    {
        _simpleReviewNote = note;
        _simpleReviewNoteTone = tone;
        OnPropertyChanged(nameof(SimpleReviewNote));
        OnPropertyChanged(nameof(SimpleReviewNoteTone));
        OnPropertyChanged(nameof(SimpleReviewNoteVisibility));
    }

    /// <summary>
    /// 保存当前条目（复用 HumanReviewService 写回 + 既有 ValidatorPipeline 重新校验；HardSafety 错误不会被清除）。
    /// 返回是否写入成功。
    /// </summary>
    public bool SimpleSaveReview()
    {
        var entry = _simpleReviewEntry;
        if (entry is null || IsBusy)
        {
            return false;
        }

        var draft = _simpleReviewDraft ?? string.Empty;
        if (string.IsNullOrWhiteSpace(draft))
        {
            SetSimpleReviewNote("保存失败：译文不能为空。", "block");
            return false;
        }

        var edited = !string.Equals(draft, _simpleReviewOriginal, StringComparison.Ordinal);
        var previous = entry.Translation;
        entry.Translation = draft;

        var saved = SaveReviewedEntries(new[] { entry });
        if (saved == 0)
        {
            entry.Translation = previous;   // 未写入翻译记忆 ⇒ 内存保持原样（草稿仍留在输入框里）
            SetSimpleReviewNote("保存失败：没有写入翻译记忆（原文为空或数据库被占用），详情见运行日志。", "block");
            return false;
        }

        RevalidateEntry(entry);
        _workflow.NoteReviewed(entry.Key.ToString(), edited ? ReviewOutcome.Edited : ReviewOutcome.Accepted);
        _simpleReviewOriginal = draft;
        _simpleReviewJustSaved = true;
        _simpleReviewedCount++;
        _expertReviewViewStale = true;
        _simpleReviewDisplay = BuildSimpleReviewDisplay(entry);
        NoteSimpleReviewChanged();

        if (ReviewFilter.HasHardSafetyError(entry))
        {
            SetSimpleReviewNote("已保存，但译文仍有格式错误（占位符 / 标签缺失或空译文），修正后才能部署。", "block");
        }
        else
        {
            SetSimpleReviewNote(edited ? "已保存修改 ✓" : "已确认 ✓", "ok");
        }

        RefreshSimpleMode();
        return true;
    }

    /// <summary>
    /// 通过并下一条：先保存；若仍有结构错误（HardSafety）则停在本条，否则跳到下一条待确认条目。
    /// </summary>
    public void SimpleApproveAndNext()
    {
        var entry = _simpleReviewEntry;
        if (entry is null || IsBusy)
        {
            return;
        }

        if (!SimpleSaveReview())
        {
            return;
        }

        if (ReviewFilter.HasHardSafetyError(entry))
        {
            SetSimpleReviewNote(
                "这条译文还有格式错误，修正后才能继续：" + SimpleModeFlow.IssueLine(_simpleReviewDisplay?.Issues, null)
                + "（也可以点「稍后再审」先跳过）",
                "block");
            return;
        }

        var currentIndex = _reviewAllEntries.IndexOf(entry);
        var next = SimpleModeFlow.NextPendingIndex(_reviewAllEntries, currentIndex, item => item.NeedsReview);
        ShowSimpleReviewEntry(next >= 0 ? _reviewAllEntries[next] : null);
        RefreshSimpleMode();
    }

    /// <summary>稍后再审：直接进入部署（未确认条目由发布检查提示；当前草稿保留，回来还能继续）。</summary>
    public void SimpleSkipReview()
    {
        if (IsBusy)
        {
            return;
        }

        _simpleReviewSkipped = true;
        Log($"[调试] 简易模式：用户选择稍后再审（剩余 {_simplePendingReviewCount} 条待确认），先进入部署。");
        RefreshSimpleMode();
    }

    /// <summary>从部署页回到审核。</summary>
    public void SimpleResumeReview()
    {
        if (IsBusy)
        {
            return;
        }

        _simpleReviewSkipped = false;
        RefreshSimpleMode();
    }

    public Visibility SimpleSkippedReviewVisibility
        => _simpleReviewSkipped && _simplePendingReviewCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string SimpleSkippedReviewText
        => $"还有 {SimpleModeFlow.FormatCount(_simplePendingReviewCount)} 条译文没有确认。";

    // ---------------- ③ 应用审核修改 ----------------

    /// <summary>把审核修改写进汉化文件（复用「审核后重新输出」）。</summary>
    public void SimpleApplyReviewChanges()
    {
        if (!IsBusy)
        {
            RegenerateOutput();
        }
    }

    // ---------------- ④ 部署 ----------------

    public string SimpleOutputSummaryText => _simpleOutputSummary;

    /// <summary>发布检查色调：ok / warn / block。</summary>
    public string SimpleGateTone => GateStatusText switch
    {
        "Passed" => "ok",
        "RequiresConfirmation" => "warn",
        _ => "block",
    };

    public string SimpleGateTitle => GateStatusText switch
    {
        "Passed" => "发布检查通过",
        "RequiresConfirmation" => "部署前需要你确认一下",
        "Blocked" => "有必须修正的问题，暂时不能部署",
        "未生成输出" => "还没有可以部署的汉化文件",
        _ => "发布检查结果读取失败",
    };

    public string SimpleGateDetail
    {
        get
        {
            switch (GateStatusText)
            {
                case "Passed":
                    return "汉化文件已全部核验，可以放心部署。部署前会自动备份游戏里原来的汉化文件。";
                case "RequiresConfirmation":
                    var parts = new List<string>();
                    if (GateNeedsReviewCount > 0)
                    {
                        parts.Add($"有 {SimpleModeFlow.FormatCount(GateNeedsReviewCount)} 条译文还没有人工确认");
                    }

                    if (GateWarningCount > 0)
                    {
                        parts.Add($"有 {SimpleModeFlow.FormatCount(GateWarningCount)} 条小问题提示");
                    }

                    var summary = parts.Count > 0 ? string.Join("，", parts) + "。" : "本轮输出有需要确认的提示。";
                    return summary + "这些问题不影响游戏运行，确认后可以部署；也可以先回去审核。";
                case "Blocked":
                    return "部分译文存在格式错误（例如占位符或标签缺失），部署后可能导致游戏显示异常。"
                           + "请回到审核修正，或切换到专家模式查看详情。";
                case "未生成输出":
                    return "请先完成翻译。";
                default:
                    return GateReasonText;
            }
        }
    }

    public Visibility SimpleGateConfirmVisibility => GateConfirmationVisibility;

    public bool SimpleCanDeploy
        => !IsBusy
           && IsDeployAllowed
           && (GateConfirmationVisibility != Visibility.Visible || GateConfirmationAcknowledged);

    /// <summary>部署目标：当前使用的游戏汉化目录。</summary>
    public string SimpleDeployTarget
        => !string.IsNullOrWhiteSpace(OldChineseDir) && Directory.Exists(OldChineseDir)
            ? OldChineseDir
            : string.IsNullOrWhiteSpace(GameRootDir)
                ? string.Empty
                : Path.Combine(GameRootDir, "LimbusCompany_Data", "Lang", "LLC_zh-CN");

    public string SimpleDeployTargetText => string.IsNullOrWhiteSpace(SimpleDeployTarget)
        ? "部署位置：（未找到游戏汉化目录）"
        : $"部署位置：{SimpleDeployTarget}";

    public string SimpleDoneText => LastDeployOutcomeText;

    /// <summary>
    /// 部署到游戏（调用前必须由窗口弹窗确认）。与「部署到游戏」使用同一条安全链：
    /// 输出清单 → 发布门禁 → 备份 → 原子替换 → 失败回滚；目标为当前使用的游戏汉化目录。
    /// </summary>
    public async Task<string> SimpleDeployAsync()
    {
        if (IsBusy)
        {
            return "[错误] 当前有任务正在运行，请等待完成后再部署。";
        }

        var target = OldChineseDir;
        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            return await DeployToGameAsync();
        }

        return await DeployToTargetAsync(target, "游戏汉化目录");
    }
}
