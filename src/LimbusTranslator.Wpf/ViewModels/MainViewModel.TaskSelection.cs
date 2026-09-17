using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Wpf.ViewModels;

/// <summary>
/// 主界面「任务范围」Tab 的**文件级任务选择**（第9.0C.3轮）。
///
/// 分层（与 <see cref="TaskSelection"/> 一致）：
///   1. 客观事实：<c>ProductionTranslationPlan</c> —— 用户勾选**不改变**它；
///   2. 任务范围：复用既有分类勾选（<c>CategoryStats</c>），切换时只在内存内重算文件列表；
///   3. 用户文件选择：<see cref="FileSelectionState"/>（逻辑文件级，切换范围后仍然保留）。
///
/// 本文件只做 GUI 呈现与状态转发；真正的过滤 / 统计逻辑全部在
/// <see cref="TaskSelection"/> 与 <see cref="FileSelectionState"/>（可单元测试）。
/// </summary>
public sealed partial class MainViewModel
{
    private readonly FileSelectionState _fileSelection = new();
    private readonly List<FileTaskRow> _allFileTasks = new();

    private ProductionTranslationPlan? _lastPlan;
    private bool _showFilesWithoutAi;
    private string _taskScopeText = "全部（尚未分析）";
    private string _fileSelectionSummaryText = "尚未执行 Diff 分析：点击「分析更新」后这里会列出需要处理的文件。";

    /// <summary>当前范围可见的文件任务（只含真正需要 AI 的文件；DataGrid 绑定）。</summary>
    public ObservableCollection<FileTaskRow> FileTasks { get; } = new();

    /// <summary>当前任务范围显示文本。</summary>
    public string TaskScopeText
    {
        get => _taskScopeText;
        private set { _taskScopeText = value; OnPropertyChanged(); }
    }

    /// <summary>文件 / 条目选择统计（已选择 N / 总 M，本轮将翻译 K 条）。</summary>
    public string FileSelectionSummaryText
    {
        get => _fileSelectionSummaryText;
        private set { _fileSelectionSummaryText = value; OnPropertyChanged(); }
    }

    /// <summary>是否显示"本轮无需 AI"的文件（默认关闭：100% 继承的文件不占用列表）。</summary>
    public bool ShowFilesWithoutAi
    {
        get => _showFilesWithoutAi;
        set
        {
            if (_showFilesWithoutAi == value)
            {
                return;
            }

            _showFilesWithoutAi = value;
            OnPropertyChanged();
            ApplyTaskScopeFilter(log: false);
        }
    }

    /// <summary>当前可见范围是否有可选文件（决定全选 / 全不选 / 反选按钮与提示）。</summary>
    public bool HasVisibleFileTasks => FileTasks.Count > 0;

    /// <summary>当前是否至少选中一个文件（决定「开始汉化」是否可用）。</summary>
    public bool HasSelectedFiles => FileTasks.Any(row => _fileSelection.IsSelected(row.LogicalFile));

    /// <summary>
    /// 当前可见范围内**真正被选中**的行。
    /// 第9.0C.3.1修复：一律以 <see cref="FileSelectionState"/> 为权威，
    /// **不再**读 <c>FileTaskRow.IsSelected</c>（那是 WPF 绑定会写入的属性，曾被虚拟化回写污染）。
    /// </summary>
    private List<FileTaskRow> SelectedVisibleRows()
        => FileTasks.Where(row => _fileSelection.IsSelected(row.LogicalFile)).ToList();

    /// <summary>
    /// 依据最新生产计划刷新文件任务列表（分析完成后 / 翻译前调用）。
    /// 重新分析时：仍存在的文件**保留**用户选择，新文件默认选中，消失的文件被移除。
    /// </summary>
    public void RefreshFileTasks(ProductionTranslationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _lastPlan = plan;

        // 第9.0C.17轮：分析 / 载入进度后同步「重译翻译失败的条目」按钮（失败标记只存在于当轮计划的条目上）
        CanRetranslateFailed = plan.OutputEntries.Any(entry => entry.ProviderBatchFailed);

        var summaries = TaskSelection.BuildSummaries(plan);
        _fileSelection.Reconcile(summaries.Select(summary => summary.LogicalFile));

        _allFileTasks.Clear();
        foreach (var summary in summaries)
        {
            _allFileTasks.Add(new FileTaskRow
            {
                LogicalFile = summary.LogicalFile,
                CategoryName = summary.CategoryName,
                SummaryText = summary.Describe(),
                NeedTranslateCount = summary.NeedTranslateCount,
                RequiresAi = summary.RequiresAi,
                IsSelected = _fileSelection.IsSelected(summary.LogicalFile),
                OnSelectionChanged = OnFileTaskSelectionChanged,
            });
        }

        ApplyTaskScopeFilter(log: true);
    }

    /// <summary>任务范围（分类勾选）变化：只做内存过滤，不重新分析、不写快照。</summary>
    internal void OnTaskScopeChanged() => ApplyTaskScopeFilter(log: true);

    private void OnFileTaskSelectionChanged(FileTaskRow row)
    {
        _fileSelection.Set(row.LogicalFile, row.IsSelected);
        UpdateFileSelectionStatistics();
    }

    /// <summary>
    /// 按当前任务范围（分类）过滤文件列表；**只影响呈现**，选择状态一律保留。
    /// </summary>
    private void ApplyTaskScopeFilter(bool log)
    {
        var scope = CurrentTaskScope();
        var visible = _allFileTasks
            .Where(row => scope.Count == 0 || scope.Contains(TextCategoryHelper.FromRelativePath(row.LogicalFile)))
            .Where(row => row.RequiresAi || ShowFilesWithoutAi)
            .OrderBy(row => row.LogicalFile, StringComparer.Ordinal)
            .ToList();

        FileTasks.Clear();
        foreach (var row in visible)
        {
            row.IsSelected = _fileSelection.IsSelected(row.LogicalFile);   // 与状态同步
            FileTasks.Add(row);
        }

        TaskScopeText = scope.Count == 0
            ? "全部"
            : string.Join("、", CategoryStats.Where(stat => stat.IsSelected).Select(stat => stat.DisplayName));

        UpdateFileSelectionStatistics();

        if (log)
        {
            Log($"[调试] 任务范围切换：{TaskScopeText}，可处理文件 {FileTasks.Count}，本轮已选择 {SelectedVisibleRows().Count}");
        }

        NotifyWorkflowBindings();
    }

    private HashSet<TextCategory> CurrentTaskScope()
        => CategoryStats.Where(stat => stat.IsSelected).Select(stat => stat.Category).ToHashSet();

    private void UpdateFileSelectionStatistics()
    {
        // 第9.0C.3.1修复：统计与按钮可用性一律读权威状态（FileSelectionState），
        // 不读 FileTaskRow.IsSelected —— 后者是 WPF 绑定写入的，曾被行虚拟化回写污染成"全部未选"。
        var selected = SelectedVisibleRows();
        var skipped = FileTasks.Where(row => !_fileSelection.IsSelected(row.LogicalFile)).ToList();
        var skippedUnits = skipped.Sum(row => row.NeedTranslateCount);
        var selectedUnits = selected.Sum(row => row.NeedTranslateCount);

        // 第9.0C.6轮：明确区分「需要 AI 的文件数」与「输出将写出的文件数」
        var outputFiles = ResolveTaskSelection()?.SelectedOutputFileCount;

        FileSelectionSummaryText =
            $"已选择 {selected.Count} / {FileTasks.Count} 个文件\n"
            + $"本轮将翻译 {selectedUnits} 条"
            + (outputFiles is null ? string.Empty : $"\n输出将写出 {outputFiles} 个文件（含无需 AI、沿用既有译文的文件）")
            + (skipped.Count > 0 ? $"\n本轮暂不处理：{skipped.Count} 个文件 / {skippedUnits} 条" : string.Empty);

        OnPropertyChanged(nameof(HasSelectedFiles));
        OnPropertyChanged(nameof(HasVisibleFileTasks));
        // 第9.0C.3轮：0 个文件被选中时「开始汉化」必须禁用（§四十）。
        OnPropertyChanged(nameof(CanTranslateAction));
    }

    /// <summary>全选**当前范围可见**文件（不影响其它范围内的隐藏选择）。</summary>
    public void SelectAllVisibleFiles()
    {
        if (FileTasks.Count == 0)
        {
            StatusText = "当前任务范围没有需要 AI 翻译的文件。";
            Log("[调试] 全选未执行：当前范围没有可处理文件。");
            return;
        }

        _fileSelection.SelectAll(FileTasks.Select(row => row.LogicalFile));
        SyncRowsFromSelection();
        Log($"[调试] 已全选当前范围 {FileTasks.Count} 个文件。");
    }

    /// <summary>全不选**当前范围可见**文件（不影响其它范围内的隐藏选择）。</summary>
    public void SelectNoneVisibleFiles()
    {
        if (FileTasks.Count == 0)
        {
            StatusText = "当前任务范围没有需要 AI 翻译的文件。";
            return;
        }

        _fileSelection.SelectNone(FileTasks.Select(row => row.LogicalFile));
        SyncRowsFromSelection();
        Log($"[调试] 已全不选当前范围 {FileTasks.Count} 个文件（开始汉化将不可用）。");
    }

    /// <summary>反选**当前范围可见**文件（不影响其它范围内的隐藏选择）。</summary>
    public void InvertVisibleFiles()
    {
        if (FileTasks.Count == 0)
        {
            StatusText = "当前任务范围没有需要 AI 翻译的文件。";
            return;
        }

        _fileSelection.Invert(FileTasks.Select(row => row.LogicalFile));
        SyncRowsFromSelection();
        Log($"[调试] 已反选当前范围 {FileTasks.Count} 个文件。");
    }

    private void SyncRowsFromSelection()
    {
        foreach (var row in FileTasks)
        {
            // 第9.0C.15轮：静默同步（不回写、不重算统计）——批量操作只需最后算一次，
            // 否则 2000 行会触发 2000 次统计重算，并且容易与行虚拟化互相覆盖。
            row.SetSelectedSilently(_fileSelection.IsSelected(row.LogicalFile));
        }

        UpdateFileSelectionStatistics();
    }

    // ───────── 第9.0C.15轮：右键菜单 / Ctrl+Shift 多选的批量操作 ─────────

    /// <summary>右键菜单：把**选中行**标记为「本轮处理」（只影响这些文件，其余不动）。</summary>
    public void MarkFilesSelected(IReadOnlyList<FileTaskRow> rows)
        => ApplyRowSelection(rows, files => _fileSelection.SelectAll(files), "本轮处理");

    /// <summary>右键菜单：把**选中行**标记为「本轮不处理」。</summary>
    public void MarkFilesUnselected(IReadOnlyList<FileTaskRow> rows)
        => ApplyRowSelection(rows, files => _fileSelection.SelectNone(files), "本轮不处理");

    /// <summary>右键菜单：对**选中行**执行反选。</summary>
    public void InvertFilesSelection(IReadOnlyList<FileTaskRow> rows)
        => ApplyRowSelection(rows, files => _fileSelection.Invert(files), "反选");

    private void ApplyRowSelection(
        IReadOnlyList<FileTaskRow> rows,
        Action<IEnumerable<string>> apply,
        string action)
    {
        if (rows is null || rows.Count == 0)
        {
            StatusText = "请先在列表里选择文件（可按住 Ctrl / Shift 多选，或用右键菜单）";
            Log($"[调试] 批量{action}未执行：没有选中任何行");
            return;
        }

        var files = rows.Select(row => row.LogicalFile).ToList();
        apply(files);
        SyncRowsFromSelection();
        Log($"[调试] 批量{action}：{files.Count} 个文件（当前范围已选择 {SelectedVisibleRows().Count}/{FileTasks.Count}）");
    }

    /// <summary>
    /// **唯一的任务选择解析入口**：GUI 统计 / 提取 / 翻译 / 输出都消费同一份结果。
    /// 返回 null 表示尚未分析（没有计划），调用方应提示用户先分析。
    /// </summary>
    public TaskSelectionResult? ResolveTaskSelection(ProductionTranslationPlan? plan = null)
    {
        var target = plan ?? _lastPlan;
        return target is null ? null : TaskSelection.Resolve(target, CurrentTaskScope(), _fileSelection);
    }

    /// <summary>翻译开始前的选择日志（不刷每行 Checkbox）。</summary>
    private void LogTaskSelection(TaskSelectionResult selection)
    {
        // 第9.0C.6轮：两件事必须分开说清楚 ——
        //   ① 用户勾选的、真正需要 AI 的文件数（与 GUI 一致）
        //   ② Merge/ReleaseGate 实际会写出多少文件（含"无需 AI、沿用既有译文"的文件，通常远大于 ①）
        Log($"[调试] 开始翻译：本轮处理文件 {selection.SelectedFiles.Count}（需要 AI），待翻译条目 {selection.SelectedEntries.Count}"
            + $"；输出将写出 {selection.SelectedOutputFileCount} 个文件");
        if (selection.IsPartial)
        {
            Log($"[调试] 用户本轮暂不处理：文件 {selection.UnselectedFileCount}，条目 {selection.UnselectedUnitCount}");
        }
    }

    /// <summary>
    /// 分析完成后构建任务文件列表（第9.0C.3轮）。
    ///
    /// 说明：这里构建的生产计划与「开始汉化」使用的是**同一个** <c>ProductionTranslationPlanBuilder</c>，
    /// 因此文件列表 / 统计 / 提取 / 翻译 / 输出天然同一份事实来源。
    /// 构建只做内存分组（不重新扫描游戏目录、不重新 Diff）；
    /// 若旧中文需要额外文件（P7 的 KR-only 文件），只解析那几个文件。
    /// </summary>
    private void RefreshFileTasksFromAnalysis(ProductionAnalyzeResult result)
    {
        if (result.Capture is null)
        {
            return;
        }

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var configDir = Path.Combine(FindProjectRoot(), "config");
            var mode = ResolveCurrentTranslationMode(configDir);
            var scope = OldChineseScopeLoader.Load(
                mode, result.DiffResult.OldChineseUnits, result.Capture, OldChineseDir, configDir);
            var plan = ProductionTranslationPlanBuilder.Build(
                mode, result.DiffResult.Entries, result.Capture, NewEnglishDir, scope.Units, _activeGlossarySnapshot);

            RefreshFileTasks(plan);
            Log($"[调试] 任务文件列表：{FileTasks.Count} 个可处理文件（构建耗时 {watch.ElapsedMilliseconds} ms）");
        }
        catch (Exception ex)
        {
            Log($"[调试] 任务文件列表构建失败（不影响分析结果）：{ex.Message}");
        }
    }
}

/// <summary>
/// 文件任务行（第9.0C.3轮）：GUI 与选择状态之间的薄适配层。
/// </summary>
public sealed class FileTaskRow : INotifyPropertyChanged
{
    private bool _isSelected = true;

    /// <summary>逻辑文件路径（= <c>UnitKey.RelativeFilePath</c>）。</summary>
    public required string LogicalFile { get; init; }

    /// <summary>分类显示名（中文）。</summary>
    public required string CategoryName { get; init; }

    /// <summary>统计摘要文本。</summary>
    public required string SummaryText { get; init; }

    /// <summary>该文件待翻译条目数。</summary>
    public required int NeedTranslateCount { get; init; }

    /// <summary>是否真正需要 AI（false = 纯继承，默认不显示）。</summary>
    public required bool RequiresAi { get; init; }

    /// <summary>选择变化回调（由 ViewModel 写回 <see cref="FileSelectionState"/>）。</summary>
    public Action<FileTaskRow>? OnSelectionChanged { get; init; }

    /// <summary>是否本轮处理。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            OnSelectionChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// 静默同步勾选状态（第9.0C.15轮）：只刷新显示，**不回写 ViewModel、不重算统计**。
    ///
    /// 用于 <c>SyncRowsFromSelection</c> 批量同步（全选/全不选/反选/右键菜单）。
    /// 旧实现逐行走 <see cref="IsSelected"/>，每行都会触发一次"回写 + 统计重算"，
    /// 文件多时既慢又容易与行虚拟化竞争。
    /// </summary>
    public void SetSelectedSilently(bool value)
    {
        if (_isSelected == value)
        {
            return;
        }

        _isSelected = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;
}
