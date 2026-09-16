using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.CharacterStyle;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Snapshots;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.Diagnostics;
using System.Windows.Media;
using LimbusTranslator.Infrastructure.Validation;
using LimbusTranslator.Infrastructure.Review;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Translation;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 主窗口 ViewModel。
/// 承担：路径配置、Diff 分析、日志记录、结果展示。
/// </summary>
public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly DiffWorkflowService _service;
    private bool _isBusy;
    private string _oldEnglishDir = string.Empty;
    private string _oldChineseDir = string.Empty;
    private string _newEnglishDir = string.Empty;
    private int _addedCount;
    private int _modifiedCount;
    private int _unchangedCount;
    private int _deletedCount;
    private int _missingCount;
    private int _totalCount;
    private string _statusText = "就绪";
    private int _fileNewCount;
    private int _fileMissingCount;
    private int _fileModifiedCount;
    private int _fileUnchangedCount;
    private int _fileDeletedCount;
    private int _extractedCount;
    private string _gameRootDir = string.Empty;
    private int _reviewCount;
    private double _progressValue;
    private string _progressText = string.Empty;
    private string _progressOperation = "等待任务";
    private bool _isProgressIndeterminate;
    private bool _isProgressActive;
    private string _logText = string.Empty;
    private readonly StringBuilder _logBuilder = new();
    private IReadOnlyList<FileDiffEntry> _categorizedFileEntries = Array.Empty<FileDiffEntry>();
    private readonly Dictionary<TextCategory, int> _categoryNewCount = new();
    private readonly Dictionary<TextCategory, int> _categoryMissingCount = new();

    /// <summary>分类统计条目（用于 UI 展示各分类 Diff 统计）</summary>
    public ObservableCollection<CategoryStat> CategoryStats { get; } = new();

    /// <summary>增量术语库条目</summary>
    public ObservableCollection<IncrementalTermEntry> IncrementalTerms { get; } = new();

    private string _incrementalTermStatus = "尚未扫描";

    /// <summary>增量术语库状态文字</summary>
    public string IncrementalTermStatus
    {
        get => _incrementalTermStatus;
        set { _incrementalTermStatus = value; OnPropertyChanged(); }
    }

    /// <summary>分类显示名映射（供 XAML 使用）</summary>
    public string GetCategoryName(TextCategory category) => TextCategoryHelper.GetDisplayName(category);

    public MainViewModel()
    {
        // 第8.8轮：初始化顶部状态与待确认术语（只读，不调用 API）
        RefreshEnvironmentStatus();
        LoadPendingTerms();
        // 默认使用项目内测试数据
        var root = FindProjectRoot();
        var testData = Path.Combine(root, "data", "testdata");
        _oldEnglishDir = Path.Combine(testData, "old_en");
        _oldChineseDir = Path.Combine(testData, "old_zh");
        _newEnglishDir = Path.Combine(testData, "new_en");

        _service = new DiffWorkflowService(Path.Combine(root, "config"));

        // 第3轮：启动时回显当前 output 的发布门禁状态
        RefreshReleaseGate();

        Logs = new ObservableCollection<string>();
        Entries = new ObservableCollection<DiffEntry>();
        FileEntries = new ObservableCollection<FileDiffEntry>();

        // 启动时尝试自动定位游戏目录
        var located = GameDirectoryLocator.AutoLocate();
        if (located is not null)
        {
            GameRootDir = located.GameRoot;
            // 没有"旧版本英文"时，以当前游戏英文作为对比基准
            _oldEnglishDir = located.NewEnglishDir;
            _newEnglishDir = located.NewEnglishDir;
            _oldChineseDir = located.ChineseDir;
        }
    }

    /// <summary>旧英文目录</summary>
    public string OldEnglishDir
    {
        get => _oldEnglishDir;
        set { _oldEnglishDir = value; OnPropertyChanged(); }
    }

    /// <summary>旧中文目录</summary>
    public string OldChineseDir
    {
        get => _oldChineseDir;
        set { _oldChineseDir = value; OnPropertyChanged(); }
    }

    /// <summary>新版英文目录</summary>
    public string NewEnglishDir
    {
        get => _newEnglishDir;
        set { _newEnglishDir = value; OnPropertyChanged(); }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); }
    }

    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    /// <summary>进度条值（0-100）</summary>
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    /// <summary>进度文字</summary>
    public string ProgressText
    {
        get => _progressText;
        set { _progressText = value; OnPropertyChanged(); }
    }

    /// <summary>当前正在执行或最近完成的任务名称。</summary>
    public string ProgressOperation
    {
        get => _progressOperation;
        private set { _progressOperation = value; OnPropertyChanged(); }
    }

    /// <summary>当前步骤总量未知时显示不定进度动画。</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set { _isProgressIndeterminate = value; OnPropertyChanged(); }
    }

    /// <summary>是否有任务正在执行。</summary>
    public bool IsProgressActive
    {
        get => _isProgressActive;
        private set { _isProgressActive = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 更新进度条。
    /// </summary>
    public void UpdateProgress(int done, int total, string text)
    {
        RunOnUiThread(() =>
        {
            IsProgressIndeterminate = total <= 0;
            ProgressValue = total > 0 ? Math.Clamp((double)done / total * 100, 0, 100) : 0;
            ProgressText = total > 0 ? $"{text} · {done}/{total}" : text;
        });
    }

    /// <summary>
    /// 重置进度条。
    /// </summary>
    public void ResetProgress()
    {
        RunOnUiThread(() =>
        {
            ProgressOperation = "等待任务";
            ProgressValue = 0;
            ProgressText = string.Empty;
            IsProgressIndeterminate = false;
            IsProgressActive = false;
        });
    }

    public int AddedCount { get => _addedCount; private set { _addedCount = value; OnPropertyChanged(); } }
    public int ModifiedCount { get => _modifiedCount; private set { _modifiedCount = value; OnPropertyChanged(); } }
    public int UnchangedCount { get => _unchangedCount; private set { _unchangedCount = value; OnPropertyChanged(); } }
    public int DeletedCount { get => _deletedCount; private set { _deletedCount = value; OnPropertyChanged(); } }
    public int MissingCount { get => _missingCount; private set { _missingCount = value; OnPropertyChanged(); } }
    public int TotalCount { get => _totalCount; private set { _totalCount = value; OnPropertyChanged(); } }

    public ObservableCollection<string> Logs { get; }

    /// <summary>可直接选中、复制的完整运行日志。</summary>
    public string LogText
    {
        get => _logText;
        private set { _logText = value; OnPropertyChanged(); }
    }

    public ObservableCollection<DiffEntry> Entries { get; }

    public ObservableCollection<FileDiffEntry> FileEntries { get; }

    public ObservableCollection<DiffEntry> ReviewEntries { get; } = new();

    public int ReviewCount { get => _reviewCount; private set { _reviewCount = value; OnPropertyChanged(); } }

    // ===== 文件级统计 =====
    public int FileNewCount { get => _fileNewCount; private set { _fileNewCount = value; OnPropertyChanged(); } }
    public int FileMissingCount { get => _fileMissingCount; private set { _fileMissingCount = value; OnPropertyChanged(); } }
    public int FileModifiedCount { get => _fileModifiedCount; private set { _fileModifiedCount = value; OnPropertyChanged(); } }
    public int FileUnchangedCount { get => _fileUnchangedCount; private set { _fileUnchangedCount = value; OnPropertyChanged(); } }
    public int FileDeletedCount { get => _fileDeletedCount; private set { _fileDeletedCount = value; OnPropertyChanged(); } }
    public int ExtractedCount { get => _extractedCount; private set { _extractedCount = value; OnPropertyChanged(); } }
    public string GameRootDir { get => _gameRootDir; set { _gameRootDir = value; OnPropertyChanged(); } }

    // ---------- 第3轮：发布门禁（ReleaseGate）----------

    private string _gateStatusText = "未生成输出";
    private int _gateErrorCount;
    private int _gateWarningCount;
    private int _gateNeedsReviewCount;
    private int _gateHistoricalInheritedErrorCount;
    private string _gateReasonText = "尚无输出清单。";
    private bool _gateConfirmationAcknowledged;
    private bool _isDeployAllowed;
    private Visibility _gateConfirmationVisibility = Visibility.Collapsed;

    /// <summary>发布门禁状态文本（未生成输出 / Passed / RequiresConfirmation / Blocked）</summary>
    public string GateStatusText
    {
        get => _gateStatusText;
        private set
        {
            _gateStatusText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GateStatusBrush));
            ApplyGateStatusText(value);
        }
    }

    /// <summary>QA Error 数</summary>
    public int GateErrorCount { get => _gateErrorCount; private set { _gateErrorCount = value; OnPropertyChanged(); } }

    /// <summary>QA Warning 数</summary>
    public int GateWarningCount { get => _gateWarningCount; private set { _gateWarningCount = value; OnPropertyChanged(); } }

    /// <summary>待审核条目数</summary>
    public int GateNeedsReviewCount { get => _gateNeedsReviewCount; private set { _gateNeedsReviewCount = value; OnPropertyChanged(); } }

    /// <summary>历史继承旧中文的结构安全差异条目数</summary>
    public int GateHistoricalInheritedErrorCount
    {
        get => _gateHistoricalInheritedErrorCount;
        private set { _gateHistoricalInheritedErrorCount = value; OnPropertyChanged(); }
    }

    /// <summary>门禁原因摘要（含可定位样本：文件 | UnitKey | Code | Severity | 说明）</summary>
    public string GateReasonText { get => _gateReasonText; private set { _gateReasonText = value; OnPropertyChanged(); } }

    /// <summary>部署按钮是否可用（Blocked 时禁用；DeployService 仍会二次拒绝）</summary>
    public bool IsDeployAllowed { get => _isDeployAllowed; private set { _isDeployAllowed = value; OnPropertyChanged(); } }

    /// <summary>是否需要显示「我已知晓这些警告，继续部署」勾选框</summary>
    public Visibility GateConfirmationVisibility
    {
        get => _gateConfirmationVisibility;
        private set { _gateConfirmationVisibility = value; OnPropertyChanged(); }
    }

    /// <summary>用户是否已勾选确认（仅对当前清单有效，重新输出后自动回到未确认）</summary>
    public bool GateConfirmationAcknowledged
    {
        get => _gateConfirmationAcknowledged;
        set { _gateConfirmationAcknowledged = value; OnPropertyChanged(); }
    }

    /// <summary>门禁状态颜色</summary>
    public Brush GateStatusBrush => _gateStatusText switch
    {
        "Blocked" => new SolidColorBrush(Color.FromRgb(0xB0, 0x2A, 0x37)),
        "RequiresConfirmation" => new SolidColorBrush(Color.FromRgb(0xC1, 0x77, 0x17)),
        "Passed" => new SolidColorBrush(Color.FromRgb(0x14, 0x7A, 0x62)),
        _ => new SolidColorBrush(Color.FromRgb(0x7A, 0x7A, 0x7A)),
    };

    /// <summary>添加日志，并同步到可复制的完整文本。</summary>
    public void Log(string message)
    {
        var content = message.StartsWith("[调试]", StringComparison.Ordinal)
            ? message
            : $"[调试] {message}";
        var line = $"[{DateTime.Now:HH:mm:ss}] {content}";
        RunOnUiThread(() =>
        {
            Logs.Add(line);
            _logBuilder.AppendLine(line);
            LogText = _logBuilder.ToString();
            // 第8.8轮：日志过滤（ShowDebugLogs 关闭时不显示 [调试] 行）
            OnPropertyChanged(nameof(DisplayLogText));
        });
    }

    /// <summary>开始一项任务；下一项任务一定会覆盖进度条的任务名称和进度。</summary>
    private void BeginProgress(string operationName, string initialText = "准备中")
    {
        RunOnUiThread(() =>
        {
            ProgressOperation = operationName;
            ProgressValue = 0;
            ProgressText = initialText;
            IsProgressIndeterminate = true;
            IsProgressActive = true;
        });
    }

    /// <summary>切换当前任务的步骤；用于总量未知的文件分析、输出和部署阶段。</summary>
    private void SetProgressStage(string stage)
    {
        RunOnUiThread(() =>
        {
            ProgressText = stage;
            ProgressValue = 0;
            IsProgressIndeterminate = true;
            IsProgressActive = true;
        });
    }

    /// <summary>标记当前任务完成，保留完成态直到用户启动下一项任务。</summary>
    private void CompleteProgress(string text = "已完成")
    {
        RunOnUiThread(() =>
        {
            ProgressValue = 100;
            ProgressText = text;
            IsProgressIndeterminate = false;
            IsProgressActive = false;
        });
    }

    /// <summary>标记当前任务失败，避免进度条继续显示上一项任务的进度。</summary>
    private void FailProgress(string text = "失败")
    {
        RunOnUiThread(() =>
        {
            ProgressText = text;
            IsProgressIndeterminate = false;
            IsProgressActive = false;
        });
    }

    /// <summary>清空界面日志和可复制日志文本。</summary>
    private void ClearLogs()
    {
        RunOnUiThread(() =>
        {
            Logs.Clear();
            _logBuilder.Clear();
            LogText = string.Empty;
        });
    }

    /// <summary>将跨线程的进度和日志更新切回 UI 线程。</summary>
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>
    /// 执行 Diff 分析。
    /// </summary>
    public async void RunDiff()
    {
        if (IsBusy)
        {
            return;
        }

        // 校验目录
        var dirs = new[] { OldEnglishDir, OldChineseDir, NewEnglishDir };
        if (dirs.Any(d => string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)))
        {
            Log("[调试] 错误：目录不存在或为空，请检查三个路径。");
            StatusText = "目录无效";
            return;
        }

        var oldEnglishDir = OldEnglishDir;
        var oldChineseDir = OldChineseDir;
        var newEnglishDir = NewEnglishDir;

        var projectRoot = FindProjectRoot();
        var configDir = Path.Combine(projectRoot, "config");
        var token = BeginCancellableOperation();

        IsBusy = true;
        StatusText = "分析中...";
        BeginGuiAnalyze();
        BeginProgress("Diff 分析", "准备分析目录");
        ClearLogs();
        Entries.Clear();
        FileEntries.Clear();
        Log("[调试] 开始 Diff 分析...");
        Log($"[调试] 旧英文: {oldEnglishDir}");
        Log($"[调试] 旧中文: {oldChineseDir}");
        Log($"[调试] 新版英文: {newEnglishDir}");

        try
        {
            // 第9.0C.1轮：**全部重任务在后台线程执行**（文件扫描 / 三语解析 / Canonical Diff / 旧中文范围 / 生产计划），
            // UI 线程只等待结果并做展示层映射（进度/日志/统计），因此窗口在整个分析期间保持响应。
            var service = GetAnalyzeService(configDir);
            var progress = new Progress<AnalyzeStageReport>(ApplyAnalyzeProgress);
            var result = await service.RunAsync(
                new ProductionAnalyzeRequest(
                    projectRoot,
                    oldEnglishDir,
                    oldChineseDir,
                    newEnglishDir,
                    CurrentTranslationMode,
                    configDir),
                progress,
                Log,
                token);

            token.ThrowIfCancellationRequested();

            // GUI 映射（UI 线程；只构造预览条目 + 统计）
            ApplyAnalyzeResult(result);

            Log("[调试] Diff 分析完成。");
            Log($"[调试] 条目级: 新增={result.DiffResult.AddedCount}, 修改={result.DiffResult.ModifiedCount}, "
                + $"未变化={result.DiffResult.UnchangedCount}, 删除={result.DiffResult.DeletedCount}, "
                + $"缺失旧译={result.DiffResult.MissingTranslationCount}");
            Log($"[调试] 文件级: 新增={result.FileResult.NewCount}, 缺失汉化={result.FileResult.MissingChineseCount}, "
                + $"修改={result.FileResult.ModifiedCount}, 删除={result.FileResult.DeletedCount}, "
                + $"未变化(已就绪)={result.FileResult.UnchangedCount}");
            StatusText = "分析完成";
            CompleteProgress("分析完成");
        }
        catch (OperationCanceledException)
        {
            // 取消不是错误：不写 Error、不改统计、不推进快照 baseline
            Log("[调试] Diff 分析已取消（未写任何快照，统计保持不变）。");
            StatusText = "操作已取消";
            FailProgress("已取消");
            _workflow.CompleteCancel();
        }
        catch (Exception ex)
        {
            StatusText = "分析失败";
            FailProgress("分析失败");
            FailGui("分析失败：请检查三个目录是否正确、文件是否被其他程序占用，然后重试。", ex);
        }
        finally
        {
            IsBusy = false;
            EndCancellableOperation();
            NotifyWorkflowBindings();

            // 保存本次 Diff 日志到文件（最多保留 10 个）
            try
            {
                var logFile = LogFileWriter.SaveDiffLog(
                    Path.Combine(FindProjectRoot(), "logs"),
                    Logs);
                Log($"[调试] Diff 日志已保存: {logFile}");
            }
            catch (Exception logEx)
            {
                Log($"[调试] 日志保存失败: {logEx.Message}");
            }
        }
    }

    /// <summary>
    /// 按分类统计文件数（总文件 + 需翻译文件），填充 CategoryStats。
    /// </summary>
    private void BuildCategoryStats()
    {
        CategoryStats.Clear();

        // 按分类分组（用全部文件，含未变化）
        var groups = _categorizedFileEntries
            .GroupBy(f => TextCategoryHelper.FromRelativePath(f.EnglishPath))
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in groups)
        {
            var needCount = group.Count(f => f.Kind != FileDiffKind.Unchanged);
            CategoryStats.Add(new CategoryStat
            {
                Category = group.Key,
                DisplayName = TextCategoryHelper.GetDisplayName(group.Key),
                TotalFileCount = group.Count(),
                NeedTranslateFileCount = needCount,
                IsSelected = needCount > 0,
            });
        }

        // 显示所有 6 个分类（无文件时 TotalCount=0）
        foreach (TextCategory category in Enum.GetValues<TextCategory>())
        {
            if (!CategoryStats.Any(c => c.Category == category))
            {
                CategoryStats.Add(new CategoryStat
                {
                    Category = category,
                    DisplayName = TextCategoryHelper.GetDisplayName(category),
                    TotalFileCount = 0,
                    NeedTranslateFileCount = 0,
                    IsSelected = false,
                });
            }
        }

        Log($"[调试] 分类统计: {string.Join(", ", CategoryStats.Select(c => $"{c.DisplayName}(总{c.TotalFileCount}/需译{c.NeedTranslateFileCount})"))}");

        // 第9.0C.3轮：分类（任务范围）勾选变化后立即联动文件列表。
        // 回调在统计构建完成后再挂上，避免构建过程中反复触发过滤。
        foreach (var stat in CategoryStats)
        {
            stat.SelectionChanged = OnTaskScopeChanged;
        }
    }

    /// <summary>
    /// 根据用户勾选的分类过滤需要翻译的 Diff 条目。
    /// </summary>
    private IReadOnlyList<DiffEntry> FilterBySelectedCategories(IReadOnlyList<DiffEntry> entries)
    {
        var selected = CategoryStats.Where(c => c.IsSelected).Select(c => c.Category).ToHashSet();
        return entries
            .Where(e => selected.Contains(TextCategoryHelper.FromRelativePath(e.Key.RelativeFilePath)))
            .ToList();
    }

    /// <summary>
    /// 全选所有分类。
    /// </summary>
    public void SelectAllCategories()
    {
        if (CategoryStats.Count == 0)
        {
            StatusText = "暂无可选分类，请先执行 Diff 分析";
            Log("[调试] 全选分类未执行：当前没有可选分类。");
            return;
        }

        // 第9.0C.3轮：批量修改时先摘掉回调，最后统一联动一次（避免逐分类反复过滤文件列表）。
        foreach (var c in CategoryStats)
        {
            c.SelectionChanged = null;
        }

        foreach (var c in CategoryStats)
        {
            c.IsSelected = true;
        }

        foreach (var c in CategoryStats)
        {
            c.SelectionChanged = OnTaskScopeChanged;
        }

        ApplyTaskScopeFilter(log: true);
        StatusText = $"已全选 {CategoryStats.Count} 个分类";
        Log($"[调试] 已全选任务范围中的 {CategoryStats.Count} 个分类。");
    }

    /// <summary>
    /// 取消全选所有分类。
    /// </summary>
    public void DeselectAllCategories()
    {
        if (CategoryStats.Count == 0)
        {
            StatusText = "暂无可选分类，请先执行 Diff 分析";
            Log("[调试] 清空分类未执行：当前没有可选分类。");
            return;
        }

        // 第9.0C.3轮：批量修改时先摘掉回调，最后统一联动一次（避免逐分类反复过滤文件列表）。
        foreach (var c in CategoryStats)
        {
            c.SelectionChanged = null;
        }

        foreach (var c in CategoryStats)
        {
            c.IsSelected = false;
        }

        foreach (var c in CategoryStats)
        {
            c.SelectionChanged = OnTaskScopeChanged;
        }

        ApplyTaskScopeFilter(log: true);
        StatusText = "已清空任务范围分类";
        Log("[调试] 已清空任务范围中的所有分类。");
    }


    /// <summary>
    /// 自动定位游戏目录。
    /// </summary>
    public async void LocateGameDir()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "正在定位游戏目录...";
        BeginProgress("自动定位游戏目录", "正在搜索游戏安装位置");
        Log("[调试] 正在自动定位游戏目录...");
        try
        {
            var located = await Task.Run(GameDirectoryLocator.AutoLocate);
            if (located is null)
            {
                Log("[调试] 自动定位失败，请手动选择目录。");
                StatusText = "定位失败";
                FailProgress("未找到游戏目录");
                return;
            }

            GameRootDir = located.GameRoot;
            OldEnglishDir = located.NewEnglishDir;   // 无旧英文时用当前英文作为基准
            OldChineseDir = located.ChineseDir;
            NewEnglishDir = located.NewEnglishDir;

            Log($"[调试] 游戏根目录: {located.GameRoot}");
            Log($"[调试] 英文目录: {located.NewEnglishDir}");
            Log($"[调试] 汉化目录: {located.ChineseDir}");
            StatusText = "已定位游戏目录";
            CompleteProgress("已定位游戏目录");
        }
        catch (Exception ex)
        {
            Log($"[调试] 自动定位失败: {ex.Message}");
            StatusText = "定位失败";
            FailProgress("定位失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 将需要汉化的文件（新增/缺失/修改）提取到待翻译目录。
    /// </summary>
    public async void ExtractNewFiles()
    {
        if (IsBusy)
        {
            return;
        }

        var pendingDir = Path.Combine(FindProjectRoot(), "data", "work", "pending");
        var oldEnglishDir = OldEnglishDir;
        var oldChineseDir = OldChineseDir;
        var newEnglishDir = NewEnglishDir;
        IsBusy = true;
        StatusText = "提取中...";
        BeginProgress("提取待汉化文件", "准备分析文件");
        Log($"[调试] 正在提取需要汉化的文件到: {pendingDir}");

        try
        {
            var result = await Task.Run(() =>
            {
                SetProgressStage("分析需要处理的文件");
                var fileResult = _service.AnalyzeFiles(oldEnglishDir, oldChineseDir, newEnglishDir);
                SetProgressStage("复制待汉化文件");
                var extractResult = NewFileExtractor.Extract(newEnglishDir, fileResult.Entries, pendingDir);
                return (FileResult: fileResult, ExtractResult: extractResult);
            });
            var extractResult = result.ExtractResult;

            ExtractedCount = extractResult.CopiedCount;
            Log($"[调试] 提取完成: {extractResult.CopiedCount} 个文件");
            foreach (var f in extractResult.Files.Take(20))
            {
                Log($"[调试]   -> {f}");
            }
            if (extractResult.Files.Count > 20)
            {
                Log($"[调试]   ... 其余 {extractResult.Files.Count - 20} 个文件省略");
            }
            StatusText = $"已提取 {extractResult.CopiedCount} 个文件";
            CompleteProgress($"已提取 {extractResult.CopiedCount} 个文件");
        }
        catch (Exception ex)
        {
            Log($"[调试] 提取失败: {ex.Message}");
            StatusText = "提取失败";
            FailProgress("提取失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 执行汉化翻译。
    /// 有 API Key 用 DeepSeek，否则用 Mock 模拟（便于验证流程）。
    /// </summary>
    public async void RunTranslate()
    {
        if (IsBusy)
        {
            return;
        }

        var dirs = new[] { OldEnglishDir, OldChineseDir, NewEnglishDir };
        if (dirs.Any(d => string.IsNullOrWhiteSpace(d) || !Directory.Exists(d)))
        {
            Log("[调试] 错误：目录不存在，请先设置三个路径。");
            StatusText = "目录无效";
            return;
        }

        var oldEnglishDir = OldEnglishDir;
        var oldChineseDir = OldChineseDir;
        var newEnglishDir = NewEnglishDir;
        var token = BeginCancellableOperation();

        IsBusy = true;
        StatusText = "翻译中...";
        BeginGuiTranslate();
        BeginProgress("增量汉化", "准备差异分析");
        ClearLogs();
        Log("[调试] 开始汉化...");

        try
        {
            // 1) 先做条目级 Diff（后台线程；取消令牌已接通）
            SetProgressStage("计算待翻译差异");
            token.ThrowIfCancellationRequested();
            var diffResult = await Task.Run(() => _service.Analyze(oldEnglishDir, oldChineseDir, newEnglishDir, null), token);

            // 第9.0B-P3轮：候选枚举 / 动作接线 / 动作过滤统一走 ProductionTranslationPlanBuilder（WPF / CLI / 测试同源）。
            //            因此这里不再提前过滤动作，也不在这里做统计与分类过滤（都改到接线之后）。

            // 2) 配置校验（第7轮 fail-closed）：配置无效 → 不创建 Provider / TM / Cache，直接停止。
            //    禁止用 Mock 掩盖配置错误（否则界面会显示“翻译成功”，实际什么都没翻译）。
            var projectRoot = FindProjectRoot();
            var configDir = Path.Combine(projectRoot, "config");
            var settings = AppSettingsLoader.LoadProviderSettings(configDir);
            if (!settings.Success)
            {
                Log("[错误] 无法启动翻译：翻译配置无效。");
                foreach (var error in settings.Errors)
                {
                    Log($"[错误]   配置错误：{error}");
                }

                Log("[错误] 已停止翻译：不会自动改用模拟翻译（Mock）。请修正 config/appsettings.json 后重试。");
                StatusText = "配置错误，翻译未启动";
                FailProgress("配置错误，翻译未启动");
                return;
            }

            var runContext = TranslationRunContext.Create();
            Log($"[调试] 本次运行 RunId: {runContext.RunId}");

            // 第4轮：request_cache / Trace（TM 命中不会进入 request_cache）
            var tmDbPath = Path.Combine(projectRoot, "data", "cache", "translation_memory.db");
            using var memory = new SqliteTranslationMemory(
                new TranslationMemoryOptions { DatabasePath = tmDbPath },
                msg => Log(msg));
            var traceWriter = new TranslationTraceWriter(projectRoot, runContext, msg => Log(msg));
            var cacheServices = TranslationCacheServices.Create(memory, runContext, traceWriter, msg => Log(msg));
            Log($"[调试] Request Cache: {tmDbPath}");
            Log($"[调试] 翻译 Trace: {traceWriter.FilePath}");

            // 第7轮：Provider 只由配置决定（Mock 必须被显式选择），并由启动助手统一输出来源日志
            var bootstrap = TranslationRunBootstrap.CreateProvider(settings, configDir, cacheServices, msg => Log(msg));
            if (!bootstrap.CanStart || bootstrap.Provider is null)
            {
                StatusText = "配置错误，翻译未启动";
                FailProgress("配置错误，翻译未启动");
                return;
            }

            var provider = bootstrap.Provider;

            // 第5轮：邻句上下文索引（每次运行只构建一次；失败回退为无上下文）
            // 第9.0B 最终轮：来源语言与模式一致（KR_JP → 日文；KR_ONLY → 韩文；EN_ONLY / KR_EN → 英文）
            var neighborUnits = ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(
                bootstrap.TranslationMode, _lastCapture, diffResult.NewUnits);
            var contextBuilder = BuildContextBuilder(neighborUnits);
            // 第8.85轮：保留运行期上下文索引，供 Review 详情只读展示 Neighbor
            _reviewContextBuilder = contextBuilder;

            // 第8.875/8.88轮：本次运行固定术语快照 → 供 Validator 使用（Prompt 与 Validator 同源）
            _activeGlossarySnapshot = bootstrap.GlossarySnapshot;
            if (bootstrap.GlossaryMerge is not null)
            {
                Log($"[调试] {bootstrap.GlossaryMerge.Describe()}");
            }

            // 第9.0B-P3轮：统一生产顺序（修复 P1-1 / P1-2）
            //   候选（英文输出结构 + KR 独有 Key，排除 SkipDeleted）
            //   → ApplyToEntries（EN_ONLY 内部直接返回；KR 模式由 Canonical 韩文 Diff 决定最终动作）
            //   → 按**最终动作**过滤 → 只有 TranslateNew / TranslateModified / TranslateMissing 进入 Agent
            // 第9.0B-P7轮：旧中文解析范围（EN_ONLY → EN 权威；KR 三模式 → Current EN ∪ Current KR 文件集，修复 P2-η）
            var oldChineseScope = OldChineseScopeLoader.Load(
                bootstrap.TranslationMode,
                diffResult.OldChineseUnits,
                _lastCapture,
                oldChineseDir,
                configDir);
            Log($"[调试] {oldChineseScope.Describe()}");

            var plan = ProductionTranslationPlanBuilder.Build(
                bootstrap.TranslationMode,
                diffResult.Entries,
                _lastCapture,
                newEnglishDir,
                oldChineseScope.Units,
                bootstrap.GlossarySnapshot);
            Log($"[调试] 术语匹配（单次共享）：注入条目 {plan.MatchedTermsInjectedCount} 条");
            Log($"[调试] {plan.Describe()}");
            if (!plan.HasCanonicalCapture)
            {
                Log("[调试] 未获得本次分析的三语捕获结果：四模式接线已跳过（KR 模式将退回纯源文语义）。");
            }

            // 第9.0C.3轮：任务范围（分类）∩ 用户文件勾选 → SelectedNeedTranslate（唯一数据源）
            RefreshFileTasks(plan);
            var taskSelection = ResolveTaskSelection(plan);
            if (taskSelection is null)
            {
                StatusText = "任务选择不可用，请先执行 Diff 分析";
                CompleteProgress("任务选择不可用");
                return;
            }

            var toTranslate = taskSelection.SelectedEntries.ToList();
            LogTaskSelection(taskSelection);

            // 第8.8轮：直通 / 真正需要 AI 统计（复用既有分类器）
            ApplyDiffStatistics(toTranslate);

            Log($"[调试] 需要翻译的条目: {toTranslate.Count} / 接线后需译 {plan.NeedTranslate.Count}");
            Log($"[调试] 已选分类: {string.Join(", ", CategoryStats.Where(c => c.IsSelected).Select(c => c.DisplayName))}");
            Log($"[调试] 可直接继承的条目: {plan.InheritCount}（其中接线后新继承 {plan.InheritedKeptCount}）");

            if (toTranslate.Count == 0)
            {
                Log("[调试] 所选任务范围 / 文件选择中没有需要翻译的新内容。");
                StatusText = FileTasks.Count > 0 && !HasSelectedFiles
                    ? "请至少选择一个需要处理的文件。"
                    : "所选范围无需翻译";
                CompleteProgress(StatusText);
                return;
            }

            UpdateProgress(0, toTranslate.Count, "准备翻译");

            try
            {
                // 3) 使用 Coordinator 多 Agent 并发执行翻译
                var coordinator = new Coordinator(
                    provider, memory,
                    maxConcurrentAgents: 8,
                    maxConcurrentApiRequests: settings.Options.MaxConcurrentRequests,
                    // 第2轮：统一 ValidatorPipeline（术语需求来自 config/glossary.json）
                    validation: CreateValidationPipeline(),
                    cacheServices: cacheServices,
                    contextBuilder: contextBuilder,
                    log: msg => Log(msg));

                // 需要翻译的条目（已按分类过滤）
                var needTranslate = toTranslate;
                Log($"[调试] 需要翻译的条目: {needTranslate.Count}");

                var coordResult = await coordinator.ExecuteAsync(needTranslate,
                    (done, total, stage) =>
                    {
                        UpdateProgress(done, total, $"翻译 {stage}");
                        if (done % 10 == 0 || done == total)
                        {
                            Log($"[调试] Stage 进度: {done}/{total}  当前: {stage}");
                        }
                    },
                    token);

                Log($"[调试] Agent 汇总: 成功 {coordResult.SuccessCount}, 失败 {coordResult.FailedCount}, 总翻译 {coordResult.TotalTranslated}");
                // 第8.8轮：汇总本轮运行统计 / Token 统计 / Thinking 统计（复用既有计数与 Trace）
                ApplyRunSummary(
                    coordResult, needTranslate.Count, traceWriter.FilePath,
                    CountThinkingDecisions(needTranslate, TranslationThinkingPolicy.FromOptions(settings.Options)));
                foreach (var failed in coordResult.Agents.Where(a => !a.IsSuccess))
                {
                    Log($"[调试]   失败 Stage: {failed.StageId} - {failed.Error}");
                }

                // 4) 合并译文（仅本轮选中的任务范围 / 文件）
                //    第9.0B-P4轮：条目集合来自生产计划的 output 条目（EN_ONLY → 英文 Key 集；KR 三模式 → 韩文 Key 集）
                //    第9.0C.3轮：Merge 与 ReleaseGate 的权威 Key 集同样来自任务选择（同一份，未选文件不会进本次 output）
                var selectedEntries = taskSelection.SelectedOutputEntries.ToList();
                var finalTranslations = Coordinator.CollectTranslations(selectedEntries);

                // 填充待审核列表（仅选中分类）
                var reviewEntries = selectedEntries.Where(e => e.NeedsReview).ToList();
                ReviewCount = reviewEntries.Count;
                // 第8.85轮：待审核列表刷新后立即应用筛选（只读投影）
                ApplyReviewFilter();
                ReviewEntries.Clear();
                foreach (var r in reviewEntries)
                {
                    ReviewEntries.Add(r);
                }

                // 更新条目表格的译文（仅选中分类）
                foreach (var e in selectedEntries)
                {
                    if (finalTranslations.TryGetValue(e.Key.ToString(), out var trans))
                    {
                        e.Translation = trans;
                    }
                }
                Entries.Clear();
                foreach (var e in plan.OutputEntries.Take(200))
                {
                    Entries.Add(e);
                }

                // 5) 输出新版中文文件到 data/output/，每个文件均需通过全字段写后校验。
                SetProgressStage("生成并核验 output");
                var outputResult = await Task.Run(() => MergeAndRecordOutput(selectedEntries, finalTranslations, "汉化输出", plan));
                var isComplete = coordResult.FailedCount == 0 && outputResult.IsComplete;
                StatusText = isComplete
                    ? $"翻译完成并已核验输出: {outputResult.WrittenFileCount} 个文件"
                    : $"翻译完成但输出不完整: 已核验 {outputResult.WrittenFileCount}/{outputResult.RequestedFileCount} 个文件";

                // 第9.0C.3轮：明确告知"本轮暂不处理"的范围（避免用户误以为系统漏翻）
                if (taskSelection.IsPartial)
                {
                    var partialNote = $"另有 {taskSelection.UnselectedFileCount} 个文件 / {taskSelection.UnselectedUnitCount} 条被你设置为「本轮暂不处理」";
                    Log($"[调试] {partialNote}（本轮为部分任务输出，未选文件保持既有汉化不变）");
                    StatusText = $"{StatusText}｜{partialNote}";
                }

                CompleteProgress(isComplete ? "翻译与输出核验完成" : "翻译完成，输出待处理");
            }
            finally
            {
                (provider as IDisposable)?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            // 第9.0C.1轮：取消翻译不是错误；已安全写入的 TM / Cache 保留，但不生成半成品输出
            Log("[调试] 翻译已取消：已停止发送新的 API 请求；已安全落库的 TM / 缓存保留，未生成半成品输出。");
            StatusText = "操作已取消";
            FailProgress("已取消");
            _workflow.CompleteCancel();
        }
        catch (Exception ex)
        {
            Log($"[调试] 汉化失败: {ex.Message}");
            StatusText = "翻译失败";
            FailProgress("翻译失败");
            FailGui("翻译失败：请检查 API 配置与网络，然后重试（已完成的译文不会丢失）。", ex);
        }
        finally
        {
            CompleteGuiTranslate();
            IsBusy = false;
            EndCancellableOperation();
            NotifyWorkflowBindings();
        }
    }

    /// <summary>
    /// 审核完成后重新输出汉化文件（使用审核修改后的译文）。
    /// </summary>
    public async void RegenerateOutput()
    {
        if (IsBusy)
        {
            return;
        }

        if (ReviewEntries.Count == 0)
        {
            Log("[调试] 没有待审核条目需要重新输出。");
            return;
        }

        Log($"[调试] 正在根据审核结果重新生成输出文件（{ReviewEntries.Count} 条审核）...");
        // 第1轮：人工确认后先把译文写回 TranslationMemory（HumanReviewed），再重新输出。
        // 写回后，下一次运行同 UnitKey + SourceHash 会直接命中，不再调用 Provider。
        PersistHumanReviewedEntries();
        await RecoverOutputFromCacheInternalAsync("审核后重新输出");
    }

    /// <summary>
    /// <summary>
    /// 把待审核列表中人工确认过的译文写回 TranslationMemory。
    /// 来源 = HumanReviewed，NeedsReview = false；空源文 / 空译文不会被写入。
    /// 写回成功后，再次运行同 UnitKey + SourceHash 会直接命中，不再调用 Provider。
    /// </summary>
    private void PersistHumanReviewedEntries()
    {
        try
        {
            var tmDbPath = Path.Combine(FindProjectRoot(), "data", "cache", "translation_memory.db");
            using var memory = new SqliteTranslationMemory(new TranslationMemoryOptions
            {
                DatabasePath = tmDbPath,
            });

            var saved = HumanReviewService.SaveReviewedEntries(ReviewEntries.ToList(), memory);
            Log($"[调试] 人工审核写回 TranslationMemory: {saved}/{ReviewEntries.Count} 条（来源=HumanReviewed，NeedsReview=false）");
            ReviewCount = ReviewEntries.Count(entry => entry.NeedsReview);
        }
        catch (Exception ex)
        {
            // 写回失败不应阻断“审核后重新输出”，只记录日志
            Log($"[调试] 人工审核写回 TranslationMemory 失败: {ex.Message}");
        }
    }

    /// 从 TranslationMemory 恢复当前英文版本可复用的译文并重新生成 output。
    /// 不调用翻译 API，适用于翻译缓存已保存但上次合并中断或失败的情况。
    /// </summary>
    public async void RecoverOutputFromCache()
    {
        if (IsBusy)
        {
            return;
        }

        Log("[调试] 正在从翻译缓存恢复 output，不会调用 API...");
        await RecoverOutputFromCacheInternalAsync("缓存恢复输出");
    }

    private async Task RecoverOutputFromCacheInternalAsync(string operationName)
    {
        IsBusy = true;
        StatusText = $"{operationName}中...";
        BeginGuiGenerateOutput();
        BeginProgress(operationName, "准备读取当前翻译状态");
        try
        {
            var selectedCategories = CategoryStats
                .Where(category => category.IsSelected)
                .Select(category => category.Category)
                .ToHashSet();
            if (selectedCategories.Count == 0)
            {
                Log("[调试] 没有选中可恢复输出的分类，请先执行 Diff 分析并勾选分类。");
                StatusText = "没有可恢复输出的分类";
                CompleteProgress("没有可恢复输出的分类");
                return;
            }

            // 审核修改优先于缓存，随后才从当前英文源文本哈希中恢复缓存译文。
            var reviewOverrides = new Dictionary<string, string>();
            foreach (var r in ReviewEntries)
            {
                if (r.Translation is not null)
                {
                    reviewOverrides[r.Key.ToString()] = r.Translation;
                }
            }

            var oldEnglishDir = OldEnglishDir;
            var oldChineseDir = OldChineseDir;
            var newEnglishDir = NewEnglishDir;

            var recoveryOutcome = await Task.Run(() =>
            {
                SetProgressStage("计算当前英文与汉化差异");
                var diffResult = _service.Analyze(oldEnglishDir, oldChineseDir, newEnglishDir, null);

                // 第9.0B-P4轮：恢复输出同样必须使用权威结构（KR 三模式 → 当前韩文）。
                // 这里**不重新捕获 Snapshot**（避免恢复路径改写基线），复用本次分析产出的 _lastCapture。
                var recoveryMode = ResolveCurrentTranslationMode(Path.Combine(FindProjectRoot(), "config"));
                var recoveryScope = OldChineseScopeLoader.Load(
                    recoveryMode,
                    diffResult.OldChineseUnits,
                    _lastCapture,
                    oldChineseDir,
                    Path.Combine(FindProjectRoot(), "config"));
                var recoveryPlan = ProductionTranslationPlanBuilder.Build(
                    recoveryMode,
                    diffResult.Entries,
                    _lastCapture,
                    newEnglishDir,
                    recoveryScope.Units,
                    _activeGlossarySnapshot);

                var selectedEntries = recoveryPlan.OutputEntries
                    .Where(entry => selectedCategories.Contains(TextCategoryHelper.FromRelativePath(entry.Key.RelativeFilePath)))
                    .ToList();

                foreach (var entry in selectedEntries)
                {
                    if (reviewOverrides.TryGetValue(entry.Key.ToString(), out var translation))
                    {
                        entry.Translation = translation;
                    }
                }

                SetProgressStage("读取翻译缓存");
                var tmDbPath = Path.Combine(FindProjectRoot(), "data", "cache", "translation_memory.db");
                using var memory = new SqliteTranslationMemory(new TranslationMemoryOptions
                {
                    DatabasePath = tmDbPath,
                });
                var recovery = OutputRecoveryService.Collect(selectedEntries, memory, CreateValidationPipeline());
                SetProgressStage("生成并核验 output");
                var outputResult = selectedEntries.Count == 0
                    ? null
                    : MergeAndRecordOutput(selectedEntries, recovery.Translations, operationName, recoveryPlan);
                return (SelectedEntryCount: selectedEntries.Count, Recovery: recovery, OutputResult: outputResult);
            });

            if (recoveryOutcome.SelectedEntryCount == 0 || recoveryOutcome.OutputResult is null)
            {
                Log("[调试] 选中分类中没有可恢复输出的条目。");
                StatusText = "没有可恢复输出的条目";
                CompleteProgress("没有可恢复输出的条目");
                return;
            }

            var recovery = recoveryOutcome.Recovery;
            var outputResult = recoveryOutcome.OutputResult;
            Log($"[调试] 缓存恢复：内存/继承 {recovery.InheritedOrInMemoryCount} 条，缓存命中 {recovery.CacheHitCount} 条，缺失 {recovery.MissingEntries.Count} 条");
            StatusText = outputResult.IsComplete
                ? $"{operationName}完成: 已核验 {outputResult.WrittenFileCount} 个文件"
                : $"{operationName}不完整: 已核验 {outputResult.WrittenFileCount}/{outputResult.RequestedFileCount} 个文件";
            CompleteProgress(outputResult.IsComplete ? "输出核验完成" : "输出核验发现问题");
        }
        catch (Exception ex)
        {
            Log($"[调试] {operationName}失败: {ex.Message}");
            StatusText = "输出失败";
            FailProgress("输出失败");
        }
        finally
        {
            CompleteGuiGenerateOutput();
            IsBusy = false;
        }
    }

    /// <summary>
    /// 读取本次运行的翻译模式（恢复输出等非 Run 路径使用；失败 → 默认 EN_ONLY 并记录错误，不静默改变结构权威）。
    /// </summary>
    private TranslationMode ResolveCurrentTranslationMode(string configDir)
    {
        if (AppSettingsLoader.TryLoadTranslationMode(configDir, out var mode, out var error))
        {
            return mode;
        }

        Log($"[错误] 翻译模式配置无效，恢复输出按默认模式（en_only）处理：{error}");
        return TranslationMode.EnglishOnly;
    }

    /// <summary>
    /// 合并译文并写入 output / 清单（第9.0B-P4轮：模板与 Expected Key 均来自生产计划的权威结构）。
    /// </summary>
    /// <param name="expectedEntries">本轮实际参与输出的条目（已按分类过滤；其 Key 集同时用于 Merge 与 Gate）</param>
    /// <param name="translations">最终译文（key → 译文）</param>
    /// <param name="operationName">操作名（日志用）</param>
    /// <param name="plan">生产翻译计划（提供权威模板目录与权威语言）</param>
    private OutputMergeResult MergeAndRecordOutput(
        IReadOnlyList<DiffEntry> expectedEntries,
        IReadOnlyDictionary<string, string> translations,
        string operationName,
        ProductionTranslationPlan plan)
    {
        var outputRoot = Path.Combine(FindProjectRoot(), "data", "output");
        var merge = new MergeOutputService();

        // 权威 Key 集：Merge 的 expectedKeys 与 Gate 的 ExpectedKeys **同一份**
        var expectedKeys = expectedEntries
            .Where(entry => entry.Action != TranslationAction.SkipDeleted)
            .Select(entry => entry.Key.ToString())
            .ToList();

        var outputResult = merge.MergeAllWithReport(
            plan.AuthoritativeDirectory,
            translations,
            outputRoot,
            expectedKeys,
            plan.AuthoritativeLanguage);

        // 第3轮：先计算发布门禁（补齐未校验的历史继承条目），再写入清单
        // 第9.0B-P4轮：Gate 使用与 Merge 完全相同的权威 Key 集（Missing / Unexpected）
        var keySet = new ReleaseGateKeySet
        {
            ExpectedKeys = expectedKeys,
            OutputKeys = outputResult.WrittenKeys,
            AuthoritativeSourceCode = SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage),
        };
        var gateResult = ReleaseGateService.Evaluate(expectedEntries, CreateValidationPipeline(), null, keySet);
        var manifest = OutputManifestService.Save(outputRoot, outputResult, gateResult);
        Log($"[调试] 输出结构权威: {SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage)}（模板目录 {plan.AuthoritativeDirectory}）");
        Log($"[调试] 发布门禁: {gateResult.Status}；Error {gateResult.ErrorCount} / Warning {gateResult.WarningCount} / 待审核 {gateResult.NeedsReviewCount} / 阻断性 Error {gateResult.BlockingErrorCount} / 历史继承结构问题 {gateResult.HistoricalInheritedErrorCount} / 缺失预期 {gateResult.MissingExpectedKeyCount} / 非预期 {gateResult.UnexpectedOutputKeyCount}");
        foreach (var reason in gateResult.Reasons)
        {
            Log($"[调试]   门禁原因 [{reason.Kind}/{reason.Code}] {reason.Message}");
            foreach (var sample in reason.Samples.Take(3))
            {
                Log($"[调试]     · {sample}");
            }
        }

        RefreshReleaseGate();

        Log($"[调试] {operationName}核验：预期文件 {outputResult.RequestedFileCount}，已写入 {outputResult.WrittenFileCount}，预期条目 {outputResult.RequestedEntryCount}，已校验 {outputResult.VerifiedEntryCount}，问题 {outputResult.Issues.Count}");
        Log($"[调试] 本轮输出清单已保存: {OutputManifestService.GetManifestPath(outputRoot)}，完整={manifest.IsComplete}");
        foreach (var file in outputResult.Files.Take(10))
        {
            Log($"[调试]   -> {file}");
        }

        foreach (var issue in outputResult.Issues.Take(20))
        {
            var target = issue.TranslationKey ?? issue.RelativeFilePath ?? "未定位目标";
            Log($"[调试] 输出问题 [{issue.Kind}] {target}: {issue.Message}");
        }

        if (outputResult.Issues.Count > 20)
        {
            Log($"[调试] 输出问题其余 {outputResult.Issues.Count - 20} 条已省略，请通过本轮输出清单和日志定位。");
        }

        return outputResult;
    }


    /// <summary>
    /// 一键部署到游戏汉化目录。
    /// 调用前必须先由 UI 弹窗确认。
    /// </summary>
    public async Task<string> DeployToGameAsync()
    {
        if (IsBusy)
        {
            return "[错误] 当前有任务正在运行，请等待完成后再部署。";
        }

        var projectRoot = FindProjectRoot();
        var outputRoot = Path.Combine(projectRoot, "data", "output");
        var backupRoot = Path.Combine(projectRoot, "data", "backup");
        IsBusy = true;
        StatusText = "部署中...";
        BeginGuiDeploy();
        BeginProgress("部署到游戏", "定位游戏汉化目录");

        try
        {
            // 自动定位游戏目录获取中文汉化目录
            var located = await Task.Run(GameDirectoryLocator.AutoLocate);
            if (located is null || !Directory.Exists(located.ChineseDir))
            {
                Log("[调试] 未找到游戏汉化目录，请先点击「自动定位游戏目录」。");
                StatusText = "未找到游戏汉化目录";
                FailProgress("未找到游戏汉化目录");
                return "[错误] 未找到游戏汉化目录，请先点击「自动定位游戏目录」。";
            }

            SetProgressStage("备份并部署已核验输出");
            // 第3轮：发布门禁确认（必须绑定当前清单 ManifestId；Blocked 不允许任何绕过）
            var gateError = await ConfirmReleaseGateAsync(outputRoot);
            if (gateError is not null)
            {
                Log($"[调试] {gateError}");
                StatusText = "发布门禁未通过";
                FailProgress("发布门禁未通过");
                return gateError;
            }

            var result = await Task.Run(() => DeployService.Deploy(outputRoot, located.ChineseDir, backupRoot));

                // 第8.85轮：记录部署结果（状态文案 / 备份目录 / 高危标记）
                ApplyDeployResult(result);

            // 第3.5轮：部署事务失败时区分“已回滚”与“回滚未完成”
            if (result.Status != DeploymentStatus.Succeeded)
            {
                Log($"[调试] 部署失败: {string.Join("；", result.Errors)}");
                Log($"[调试] 回滚状态={result.Status}，已恢复 {result.RolledBackCount} 个文件，备份目录 {result.BackupDir}");
                foreach (var rollbackError in result.RollbackErrors)
                {
                    Log($"[调试]   回滚未完成: {rollbackError}");
                }

                var failedText = result.RollbackSucceeded ? "部署失败，已恢复部署前状态" : "部署失败，回滚未完成（请勿启动游戏）";
                StatusText = failedText;
                FailProgress(failedText);
                return BuildDeployFailureMessage(result);
            }
            var deployScope = result.UsedOutputManifest ? "本轮已核验输出清单" : "兼容模式输出目录扫描";
            Log($"[调试] 部署完成: {result.DeployedCount} 个文件, 备份 {result.BackedUpCount} 个到 {result.BackupDir}，来源={deployScope}，本轮完整={result.IsOutputComplete}");
            Log($"[调试] 目标目录: {result.TargetDir}");
            foreach (var f in result.Files.Take(10))
            {
                Log($"[调试]   -> {f}");
            }
            StatusText = $"已部署 {result.DeployedCount} 个文件";
            CompleteProgress($"已部署 {result.DeployedCount} 个文件");
            return $"部署成功！{result.DeployedCount} 个文件已写入游戏汉化目录。\n\n原文件已备份到:\n{result.BackupDir}";
        }
        catch (Exception ex)
        {
            Log($"[调试] 部署失败: {ex.Message}");
            StatusText = "部署失败";
            FailProgress("部署失败");
            return $"[错误] 部署失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 选择目录。
    /// </summary>
    public void BrowseDirectory(string kind)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"请选择{GetDirLabel(kind)}",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            switch (kind)
            {
                case "old_en": OldEnglishDir = dialog.FolderName; break;
                case "old_zh": OldChineseDir = dialog.FolderName; break;
                case "new_en": NewEnglishDir = dialog.FolderName; break;
            }
            Log($"[调试] 已选择{GetDirLabel(kind)}: {dialog.FolderName}");
        }
    }

    private static string GetDirLabel(string kind) => kind switch
    {
        "old_en" => "旧英文目录",
        "old_zh" => "旧中文目录",
        "new_en" => "新版英文目录",
        _ => "目录",
    };

    /// <summary>
    /// <summary>
    /// 创建统一校验流水线（第2轮）：术语需求来自 config/glossary.json。
    /// </summary>
    /// <summary>
    /// 部署前的发布门禁确认。
    /// 返回 null 表示允许继续部署；返回字符串表示拒绝部署（[错误] 开头，可直接展示）。
    ///
    /// 规则（与 DeployService 二次硬检查一致）：
    ///   - Blocked → 永远拒绝，不提供任何人工绕过；
    ///   - RequiresConfirmation → 必须由用户勾选确认，并把确认写入当前清单（绑定 ManifestId）；
    ///   - Passed → 直接放行。
    /// </summary>
    private Task<string?> ConfirmReleaseGateAsync(string outputRoot)
    {
        try
        {
            var manifest = OutputManifestService.Load(outputRoot);
            if (manifest is null)
            {
                return Task.FromResult<string?>(
                    "[错误] 未找到输出清单（.limbus-output.manifest），无法确认发布门禁状态。请先执行「开始汉化」或「审核后重新输出」。");
            }

            if (manifest.ReleaseGateStatus == ReleaseGateStatus.Blocked)
            {
                var reasons = manifest.BlockingReasons.Count > 0
                    ? string.Join("；", manifest.BlockingReasons)
                    : "存在阻断性 QA 错误";
                return Task.FromResult<string?>($"[错误] 发布门禁阻断（Blocked），已拒绝部署：{reasons}");
            }

            if (manifest.ReleaseGateStatus != ReleaseGateStatus.RequiresConfirmation
                || manifest.HasValidConfirmation)
            {
                return Task.FromResult<string?>(null);
            }

            if (!GateConfirmationAcknowledged)
            {
                return Task.FromResult<string?>(
                    "[错误] 本轮输出存在需要人工确认的 QA 问题，请先勾选「我已知晓这些警告，继续部署」。\n\n"
                    + (manifest.RequiresConfirmationReason ?? string.Empty));
            }

            // 确认写入当前清单（绑定 ManifestId；重新输出后旧确认自动失效）
            OutputManifestService.SaveConfirmation(
                outputRoot,
                manifest.ManifestId,
                manifest.WarningCount,
                manifest.HistoricalInheritedErrorCount);

            Log($"[调试] 已记录人工确认（绑定清单 {manifest.ManifestId}）：已知晓 Warning {manifest.WarningCount} 条 / 历史继承结构问题 {manifest.HistoricalInheritedErrorCount} 条");
            RefreshReleaseGate();
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            return Task.FromResult<string?>($"[错误] 发布门禁确认失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 生成部署失败的展示文本（第3.5轮）：
    /// 回滚成功 → 明确告知已恢复部署前状态；
    /// 回滚未完成 → 高危提示 + 未恢复文件列表 + 备份位置。
    /// </summary>
    private static string BuildDeployFailureMessage(DeployResult result)
    {
        var failedPath = string.IsNullOrWhiteSpace(result.FailedRelativePath) ? "(未知)" : result.FailedRelativePath;
        var failureReason = result.Errors.Count > 0 ? string.Join("；", result.Errors) : "(未提供)";

        if (result.RollbackSucceeded)
        {
            return "[错误] 部署失败，但已恢复部署前状态。\n\n"
                + $"失败文件：{failedPath}\n"
                + $"失败原因：{failureReason}\n"
                + $"已恢复文件数：{result.RolledBackCount}\n"
                + $"备份位置：{result.BackupDir}";
        }

        return "[错误] 部署失败，并且自动回滚未全部完成。\n\n"
            + "请不要继续启动游戏或再次部署。\n\n"
            + "未恢复文件：\n" + string.Join("\n", result.UnrecoveredRelativePaths) + "\n\n"
            + "回滚错误：\n" + string.Join("\n", result.RollbackErrors) + "\n\n"
            + $"失败文件：{failedPath}\n"
            + $"失败原因：{failureReason}\n"
            + $"备份位置：{result.BackupDir}";
    }

    /// <summary>
    /// 刷新发布门禁状态（读取当前 data/output 清单）。
    /// </summary>
    private void RefreshReleaseGate()
    {
        try
        {
            var outputRoot = Path.Combine(FindProjectRoot(), "data", "output");
            var manifest = OutputManifestService.Load(outputRoot);
            if (manifest is null)
            {
                GateStatusText = "未生成输出";
                GateErrorCount = 0;
                GateWarningCount = 0;
                GateNeedsReviewCount = 0;
                GateHistoricalInheritedErrorCount = 0;
                GateReasonText = "尚无输出清单：请先执行「开始汉化」或「审核后重新输出」。";
                GateConfirmationVisibility = Visibility.Collapsed;
                IsDeployAllowed = false;
                GateConfirmationAcknowledged = false;
                return;
            }

            GateStatusText = manifest.ReleaseGateStatus.ToString();
            GateErrorCount = manifest.ErrorCount;
            GateWarningCount = manifest.WarningCount;
            GateNeedsReviewCount = manifest.NeedsReviewCount;
            GateHistoricalInheritedErrorCount = manifest.HistoricalInheritedErrorCount;
            GateReasonText = BuildGateReasonText(manifest);
            GateConfirmationVisibility = manifest.ReleaseGateStatus == ReleaseGateStatus.RequiresConfirmation
                ? Visibility.Visible
                : Visibility.Collapsed;
            IsDeployAllowed = manifest.ReleaseGateStatus != ReleaseGateStatus.Blocked;
            GateConfirmationAcknowledged = manifest.HasValidConfirmation;
        }
        catch (Exception ex)
        {
            GateStatusText = "清单读取失败";
            GateErrorCount = 0;
            GateWarningCount = 0;
            GateNeedsReviewCount = 0;
            GateHistoricalInheritedErrorCount = 0;
            GateReasonText = $"[错误] 输出清单读取失败: {ex.Message}";
            GateConfirmationVisibility = Visibility.Collapsed;
            IsDeployAllowed = false;
            GateConfirmationAcknowledged = false;
        }
    }

    /// <summary>生成可展示 / 可复制的门禁原因文本（含可定位样本）。</summary>
    private static string BuildGateReasonText(OutputRunManifest manifest)
    {
        var lines = new List<string>
        {
            $"清单: {manifest.ManifestId}（FormatVersion {manifest.FormatVersion}，完整={manifest.IsComplete}）",
            $"发布状态: {manifest.ReleaseGateStatus}",
            "Error " + manifest.ErrorCount
            + " / Warning " + manifest.WarningCount
            + " / 待审核 " + manifest.NeedsReviewCount
            + " / 阻断性 Error " + manifest.BlockingErrorCount
            + " / 历史继承结构问题 " + manifest.HistoricalInheritedErrorCount,
        };

        if (manifest.ReleaseGateStatus == ReleaseGateStatus.Blocked)
        {
            lines.Add("阻断原因:");
            foreach (var reason in manifest.BlockingReasons)
            {
                lines.Add("  - " + reason);
            }
        }

        if (!string.IsNullOrWhiteSpace(manifest.RequiresConfirmationReason))
        {
            lines.Add("需要确认: " + manifest.RequiresConfirmationReason);
        }

        foreach (var reason in manifest.GateReasons)
        {
            lines.Add($"[{reason.Kind}/{reason.Code}] {reason.Message}（{reason.Count} 条，结论 {reason.Escalation}）");
            foreach (var sample in reason.Samples)
            {
                lines.Add("    · " + sample);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 第5轮：构建 StoryData 邻句上下文构造器（索引每次运行只构建一次；失败回退为无上下文）。
    /// 绝不在 Agent / Batch / Item 级别重新解析文件。
    /// </summary>
    private TranslationContextBuilder BuildContextBuilder(IReadOnlyList<TranslationUnit> newUnits)
    {
        try
        {
            var index = TranslationContextIndex.Build(newUnits, msg => Log(msg));
            Log($"[调试] 邻句上下文索引: 作用域 {index.ScopeCount}，对话行 {index.NodeCount}（跳过 {index.SkippedUnitCount} 非 content/空文本）");
            return new TranslationContextBuilder(index);
        }
        catch (Exception ex)
        {
            Log($"[调试] 邻句上下文索引构建失败，本次运行不使用上下文: {ex.Message}");
            return TranslationContextBuilder.Disabled;
        }
    }

    /// <summary>
    /// 第8.875轮：本次运行固定的术语快照（由 TranslationRunBootstrap 创建；供 Validator 使用）。
    /// 为 null 时（例如未启动运行就做扫描）回退为按文件加载。
    /// </summary>
    private ActiveGlossarySnapshot? _activeGlossarySnapshot;

    /// <summary>
    /// 第9.0B-P0收口轮：本次分析产出的三语捕获结果（含 Sources/PreviousKoreanSources/CanonicalDiff），
    /// 第9.0B-P3轮：交给 <c>ProductionTranslationPlanBuilder.Build</c> 统一完成接线与动作过滤
    ///（WPF 不再自己调用 ApplyToEntries，生产顺序只有一份实现）。
    /// </summary>
    private MultilingualCaptureResult? _lastCapture;

    /// <summary>
    /// 创建统一校验流水线（第2轮：术语需求来自术语库）。
    /// 第8.875轮：优先使用本次运行的术语快照，保证 Prompt 与 Validator 使用同一术语版本。
    /// </summary>
    private ValidationPipeline CreateValidationPipeline()
        => _activeGlossarySnapshot is null
            ? ValidationPipeline.CreateDefault(Path.Combine(FindProjectRoot(), "config"))
            : ValidationPipeline.CreateDefault(_activeGlossarySnapshot);

    /// 向上查找项目根目录。
    /// </summary>
    private static string FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "LimbusTranslator.sln"))
                || File.Exists(Path.Combine(dir.FullName, "LimbusTranslator.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// 扫描选中分类的需翻译文件，提取候选术语。
    /// </summary>
    public async void ScanIncrementalTerms()
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            if (_lastPlan is null)
            {
                Log("[调试] 尚未完成 Diff 分析，无法确定提取范围（请先点击「分析更新」）。");
                IncrementalTermStatus = "请先执行 Diff 分析";
                return;
            }

            IsBusy = true;
            IncrementalTermStatus = "扫描中...";
            BeginProgress("增量术语扫描", "读取当前任务选择");

            // 只扫描实际需要翻译的字段，不能把 JSON 元数据、文件名或其他非翻译字符串混入术语候选。
            var scan = await Task.Run(() =>
            {
                SetProgressStage("读取当前任务选择");
                // 第9.0C.3轮：提取与"开始汉化"必须使用**同一份**任务选择（同一计划 + 同一文件勾选），
                // 不再单独重新 Analyze（否则会出现「UI 勾 5 个、提取 100 个」的错位）。
                var currentSelection = ResolveTaskSelection();
                if (currentSelection is null)
                {
                    return (EntryCount: 0, Candidates: (IReadOnlyList<TermScanCandidate>)Array.Empty<TermScanCandidate>(), NoPlan: true);
                }

                var entriesToScan = currentSelection.SelectedEntries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.NewSourceText))
                    .ToList();

                if (entriesToScan.Count == 0)
                {
                    return (EntryCount: 0, Candidates: (IReadOnlyList<TermScanCandidate>)Array.Empty<TermScanCandidate>(), NoPlan: false);
                }

                SetProgressStage("筛选需要解释的专名与固定术语");
                var configDir = Path.Combine(FindProjectRoot(), "config");
                var glossary = new GlossaryService(configDir);
                var characterStyles = new CharacterStyleService(configDir);
                var candidates = TermScanner.ScanDetailed(
                    entriesToScan.Select(entry => entry.NewSourceText!),
                    glossary.Entries,
                    minOccurrence: 2,
                    excludedTerms: characterStyles.Styles.Keys);
                return (EntryCount: entriesToScan.Count, Candidates: candidates, NoPlan: false);
            });

            if (scan.NoPlan)
            {
                Log("[调试] 尚未完成 Diff 分析，无法确定提取范围。");
                IncrementalTermStatus = "请先执行 Diff 分析";
                CompleteProgress("请先执行 Diff 分析");
                return;
            }

            if (scan.EntryCount == 0)
            {
                Log("[调试] 选中分类中没有可扫描的待翻译字段。");
                IncrementalTermStatus = "没有可扫描的待翻译字段";
                CompleteProgress("没有可扫描的待翻译字段");
                return;
            }

            Log($"[调试] 开始整理术语，待翻译字段数: {scan.EntryCount}");
            UpdateProgress(0, scan.Candidates.Count, "整理候选术语");

            // 加载现有术语库，并保留“已收录 / 新术语”状态供界面排序与筛选。
            IncrementalTerms.Clear();
            foreach (var candidate in scan.Candidates)
            {
                IncrementalTerms.Add(new IncrementalTermEntry
                {
                    OriginalText = candidate.OriginalText,
                    OccurrenceCount = candidate.OccurrenceCount,
                    SourceFileCount = candidate.SourceTextCount,
                    IsExisting = candidate.IsExisting,
                    ExistingTranslation = candidate.ExistingTranslation,
                    IsSelected = !candidate.IsExisting,
                });
            }

            var newCount = IncrementalTerms.Count(term => !term.IsExisting);
            var existingCount = IncrementalTerms.Count - newCount;
            Log($"[调试] 扫描完成：新术语 {newCount} 个，已收录 {existingCount} 个");
            IncrementalTermStatus = $"新术语 {newCount} 个，已收录 {existingCount} 个";
            UpdateProgress(scan.Candidates.Count, scan.Candidates.Count, "术语扫描");
            CompleteProgress("术语扫描完成");
        }
        catch (Exception ex)
        {
            Log($"[调试] 扫描失败: {ex.Message}");
            IncrementalTermStatus = $"扫描失败: {ex.Message}";
            FailProgress("术语扫描失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 调用 DeepSeek 批量生成术语解释（译名 + 含义 + 由来）。
    /// </summary>
    public async void ExplainIncrementalTerms()
    {
        if (IsBusy)
        {
            return;
        }
        if (IncrementalTerms.Count == 0)
        {
            Log("[调试] 请先点击「扫描术语」。");
            return;
        }

        // 第7轮：术语解释同样走 fail-closed 配置校验，不使用默认值掩盖配置错误
        var settings = AppSettingsLoader.LoadProviderSettings(Path.Combine(FindProjectRoot(), "config"));
        if (!settings.Success)
        {
            foreach (var error in settings.Errors)
            {
                Log($"[错误]   配置错误：{error}");
            }

            Log("[错误] 无法生成术语解释：配置无效（不会自动改用模拟翻译）。");
            IncrementalTermStatus = "配置错误";
            return;
        }

        if (settings.Mode == TranslationProviderMode.Mock)
        {
            Log("[调试] 当前为模拟翻译模式（provider=mock），无法生成术语解释。");
            IncrementalTermStatus = "模拟模式不支持";
            return;
        }

        var options = settings.Options;

        IsBusy = true;
        BeginProgress("生成术语解释", "准备 DeepSeek 请求");
        try
        {
            // 只对勾选、未收录且未填译名的术语生成解释。
            // 已收录项只用于对照，不允许被本功能误写回主术语库。
            var termsToExplain = IncrementalTerms
                .Where(t => !t.IsExisting && t.IsSelected && string.IsNullOrWhiteSpace(t.Translation))
                .OrderByDescending(t => t.OccurrenceCount)
                .Select(t => t.OriginalText)
                .ToList();

            if (termsToExplain.Count == 0)
            {
                Log("[调试] 没有需要解释的新术语（请先勾选新术语或清空已有译名）。");
                IncrementalTermStatus = "没有需要解释的术语";
                CompleteProgress("没有需要解释的术语");
                return;
            }

            Log($"[调试] 开始生成 {termsToExplain.Count} 个术语的解释（单批最多 {TermExplainer.MaxTermsPerRequest} 个）...");
            IncrementalTermStatus = $"生成解释中... ({termsToExplain.Count} 个)";
            UpdateProgress(0, termsToExplain.Count, "请求 DeepSeek");

            using var explainer = new TermExplainer(options);
            var explanations = await explainer.ExplainAsync(
                termsToExplain,
                (done, total) =>
                {
                    UpdateProgress(done, total, "生成术语解释");
                    RunOnUiThread(() => IncrementalTermStatus = $"生成解释中... ({done}/{total})");
                });

            var invalidSuggestionCount = 0;
            foreach (var entry in IncrementalTerms)
            {
                if (explanations.TryGetValue(entry.OriginalText, out var exp))
                {
                    if (exp.IsSuggestedTranslationValid)
                    {
                        // 推荐译名必须为一个有效译名；成功后自动写入对应译名列。
                        entry.Translation = exp.SuggestedTranslation;
                    }
                    else
                    {
                        invalidSuggestionCount++;
                    }
                    entry.Explanation = exp.FullExplanation;
                }
            }

            if (invalidSuggestionCount > 0)
            {
                Log($"[调试] 有 {invalidSuggestionCount} 个术语返回了多个或无效推荐译名，未自动回填，请人工确认。");
            }

            Log($"[调试] 术语解释完成: {explanations.Count} 个");
            IncrementalTermStatus = $"已生成 {explanations.Count} 个术语解释";
            CompleteProgress("术语解释完成");
        }
        catch (Exception ex)
        {
            Log($"[调试] 生成解释失败: {ex.Message}");
            IncrementalTermStatus = $"生成失败: {ex.Message}";
            FailProgress("术语解释失败");
        }
        finally
        {
            IsBusy = false;
        }
    }


    /// <summary>
    /// 把勾选的术语写入主术语库 glossary.json。
    /// </summary>
    public async void WriteIncrementalTerms()
    {
        if (IsBusy)
        {
            return;
        }

        var termsToWrite = IncrementalTerms
            .Where(term => !term.IsExisting && term.IsSelected && !string.IsNullOrWhiteSpace(term.Translation))
            .Select(term => (term.OriginalText, Translation: term.Translation!.Trim()))
            .ToList();
        if (termsToWrite.Count == 0)
        {
            Log("[调试] 没有可写入的术语（请勾选并填写译名）。");
            IncrementalTermStatus = "没有可写入的术语";
            return;
        }

        IsBusy = true;
        BeginProgress("写入术语库", $"准备写入 {termsToWrite.Count} 个术语");
        try
        {
            var configDir = Path.Combine(FindProjectRoot(), "config");
            var written = await Task.Run(() =>
            {
                SetProgressStage("保存 glossary.json");
                var glossary = new GlossaryService(configDir);
                var count = 0;
                var rejected = new List<string>();
                foreach (var term in termsToWrite)
                {
                    // 第8.87轮：Update 返回 false = 译名为空被拒绝（不再写入“只有原文”的坏数据）
                    if (glossary.Update(term.OriginalText, term.Translation, locked: true))
                    {
                        count++;
                    }
                    else
                    {
                        rejected.Add(term.OriginalText);
                    }
                }

                glossary.Save();
                return (Written: count, Rejected: rejected, SkippedEmpty: glossary.LastSaveSkippedEmptyKeys);
            });

            if (written.Rejected.Count > 0)
            {
                Log($"[调试] 以下 {written.Rejected.Count} 个术语因译名为空被拒绝写入: {string.Join("、", written.Rejected.Take(10))}");
            }

            if (written.SkippedEmpty.Count > 0)
            {
                Log($"[调试] 术语库存在 {written.SkippedEmpty.Count} 个空译名条目，已跳过写入（请补全译名）: {string.Join("、", written.SkippedEmpty.Take(10))}");
            }

            Log($"[调试] 已写入 {written.Written} 个术语到术语库: {configDir}\\glossary.json");
            IncrementalTermStatus = $"已写入 {written.Written} 个术语";
            CompleteProgress($"已写入 {written.Written} 个术语");
        }
        catch (Exception ex)
        {
            Log($"[调试] 写入术语库失败: {ex.Message}");
            IncrementalTermStatus = "写入失败";
            FailProgress("写入术语库失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

}
