using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>
/// 单个**逻辑文件**的任务摘要（第9.0C.3轮）。
///
/// 粒度说明：Logical File（例如 <c>StoryData/1D101A.json</c>），
/// 与 <see cref="UnitKey.RelativeFilePath"/> 完全一致 —— 不是物理文件（EN_/KR_/JP_ 三份）。
/// </summary>
public sealed record FileTaskSummary(
    string LogicalFile,
    TextCategory Category,
    int NeedTranslateCount,
    int NewCount,
    int ModifiedCount,
    int MissingCount,
    int ReviewCount,
    int InheritCount)
{
    /// <summary>是否真的需要 AI（至少一条 TranslateNew / TranslateModified / TranslateMissing）。</summary>
    public bool RequiresAi => NeedTranslateCount > 0;

    /// <summary>显示用文件路径（与 UnitKey 的 RelativeFilePath 相同）。</summary>
    public string DisplayPath => LogicalFile;

    /// <summary>分类显示名（中文）。</summary>
    public string CategoryName => TextCategoryHelper.GetDisplayName(Category);

    /// <summary>一句话摘要（GUI 副标题用）。</summary>
    public string Describe()
        => RequiresAi
            ? $"待翻译 {NeedTranslateCount}｜新增 {NewCount}｜修改 {ModifiedCount}｜缺译 {MissingCount}"
              + (ReviewCount > 0 ? $"｜需审核 {ReviewCount}" : string.Empty)
            : $"无需 AI（可继承旧翻译 {InheritCount}）";
}

/// <summary>
/// 文件级选择状态（第9.0C.3轮）—— **用户意图**，与 Diff 语义完全无关。
///
/// 规则：
///   - Key 为逻辑文件路径；<c>true</c> = 本轮处理，<c>false</c> = 本轮暂不处理；
///   - 重新分析后：仍存在的文件**保留**用户的选择，新文件**默认选中**，消失的文件被移除；
///   - 用户取消勾选**不是** TranslationAction（不产生 Inherit / SkipDeleted 等语义）。
/// </summary>
public sealed class FileSelectionState
{
    private readonly Dictionary<string, bool> _selection = new(StringComparer.Ordinal);

    /// <summary>当前状态快照（逻辑文件 → 是否本轮处理）。</summary>
    public IReadOnlyDictionary<string, bool> Snapshot => _selection;

    /// <summary>当前是否选中（未知文件视为默认选中 ⇒ 保持"什么都不做 = 旧行为"）。</summary>
    public bool IsSelected(string logicalFile)
        => !_selection.TryGetValue(logicalFile, out var selected) || selected;

    /// <summary>设置某个文件的选择。</summary>
    public void Set(string logicalFile, bool selected) => _selection[logicalFile] = selected;

    /// <summary>
    /// 依据最新分析结果收敛状态：保留已有选择、新文件默认选中、消失文件移除。
    /// </summary>
    public void Reconcile(IEnumerable<string> logicalFiles)
    {
        var current = logicalFiles.ToHashSet(StringComparer.Ordinal);
        foreach (var disappeared in _selection.Keys.Where(key => !current.Contains(key)).ToList())
        {
            _selection.Remove(disappeared);
        }

        foreach (var file in current)
        {
            if (!_selection.ContainsKey(file))
            {
                _selection[file] = true;   // 新文件默认选中
            }
        }
    }

    /// <summary>对给定可见集合执行全选（**只影响可见范围**）。</summary>
    public void SelectAll(IEnumerable<string> visibleFiles)
    {
        foreach (var file in visibleFiles)
        {
            _selection[file] = true;
        }
    }

    /// <summary>对给定可见集合执行全不选（**只影响可见范围**）。</summary>
    public void SelectNone(IEnumerable<string> visibleFiles)
    {
        foreach (var file in visibleFiles)
        {
            _selection[file] = false;
        }
    }

    /// <summary>对给定可见集合执行反选（**只影响可见范围**）。</summary>
    public void Invert(IEnumerable<string> visibleFiles)
    {
        foreach (var file in visibleFiles)
        {
            _selection[file] = !IsSelected(file);
        }
    }

    /// <summary>清空全部状态。</summary>
    public void Clear() => _selection.Clear();
}

/// <summary>
/// 任务选择结果（第9.0C.3轮）：GUI 展示 / 提取 / 翻译 / 输出**共用同一份**结果。
/// </summary>
public sealed record TaskSelectionResult(
    IReadOnlyList<DiffEntry> SelectedEntries,
    IReadOnlyList<DiffEntry> SelectedOutputEntries,
    IReadOnlyList<string> SelectedFiles,
    IReadOnlyList<string> VisibleFiles,
    int VisibleNeedTranslateUnitCount,
    int UnselectedFileCount,
    int UnselectedUnitCount,
    int TotalNeedTranslateUnitCount,
    int TotalFileCount)
{
    /// <summary>Merge 与 ReleaseGate 使用的权威 Key 集（**只含本轮选择的文件**）。</summary>
    public IReadOnlyList<string> ExpectedOutputKeys
        => SelectedOutputEntries.Select(entry => entry.Key.ToString()).ToList();

    /// <summary>本轮是否只处理了部分文件（存在"本轮暂不处理"的待翻译文件）。</summary>
    public bool IsPartial => UnselectedFileCount > 0;

    /// <summary>一句话摘要（状态栏 / 日志 / ReleaseGate 提示用）。</summary>
    public string Describe()
        => $"选中文件 {SelectedFiles.Count}/{TotalFileCount}，本轮待翻译 {SelectedEntries.Count} 条"
           + (IsPartial ? $"；本轮暂不处理 {UnselectedFileCount} 个文件 / {UnselectedUnitCount} 条" : string.Empty);
}

/// <summary>
/// 任务选择（第9.0C.3轮）—— **唯一的用户任务选择实现**。
///
/// 数据分层（禁止混用）：
///   1. <c>ProductionTranslationPlan</c>：客观上需要翻译什么（后端事实，不受用户勾选影响）；
///   2. Task Scope：用户当前在看哪个/哪些分类（复用既有分类勾选，不另立模型）；
///   3. <see cref="FileSelectionState"/>：用户本轮真正想处理哪些文件（GUI 意图）。
///
/// 本类只做**内存过滤**：不重新扫描游戏目录、不重新跑 Diff / CanonicalDiff / ParseDirectory，
/// 也不修改任何 <see cref="DiffEntry"/> 的动作语义。
/// </summary>
public static class TaskSelection
{
    /// <summary>
    /// 从生产计划构建**逻辑文件级**摘要（只需构建一次，供 GUI 过滤 / 统计）。
    /// </summary>
    public static IReadOnlyList<FileTaskSummary> BuildSummaries(ProductionTranslationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // 需要 AI 的文件（NeedTranslate，逐条动作沿用后端唯一谓词）
        var needFiles = plan.NeedTranslate
            .GroupBy(entry => entry.Key.RelativeFilePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        // 纯继承（无需 AI）的文件，用于"显示无需 AI 处理的文件"与输出范围
        var inheritFileCounts = plan.OutputEntries
            .Where(entry => !ProductionTranslationPlanBuilder.IsTranslationRequired(entry))
            .GroupBy(entry => entry.Key.RelativeFilePath, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var allFiles = new HashSet<string>(needFiles.Keys, StringComparer.Ordinal);
        foreach (var file in inheritFileCounts.Keys)
        {
            allFiles.Add(file);
        }

        return allFiles
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => Build(file, needFiles, inheritFileCounts))
            .ToList();
    }

    private static FileTaskSummary Build(
        string file,
        IReadOnlyDictionary<string, List<DiffEntry>> needFiles,
        IReadOnlyDictionary<string, int> inheritFileCounts)
    {
        var entries = needFiles.TryGetValue(file, out var list) ? list : new List<DiffEntry>();
        return new FileTaskSummary(
            LogicalFile: file,
            Category: TextCategoryHelper.FromRelativePath(file),
            NeedTranslateCount: entries.Count,
            NewCount: entries.Count(entry => entry.Action == TranslationAction.TranslateNew),
            ModifiedCount: entries.Count(entry => entry.Action == TranslationAction.TranslateModified),
            MissingCount: entries.Count(entry => entry.Action == TranslationAction.TranslateMissing),
            ReviewCount: entries.Count(entry => entry.NeedsReview),
            InheritCount: inheritFileCounts.TryGetValue(file, out var inherit) ? inherit : 0);
    }

    /// <summary>
    /// 解析本轮任务选择：任务范围（分类）→ 用户文件勾选 → SelectedNeedTranslate。
    /// </summary>
    /// <param name="plan">生产计划（客观事实）</param>
    /// <param name="scopeCategories">当前任务范围（用户勾选的分类；空集 = 全部）</param>
    /// <param name="selection">文件选择状态（用户意图）</param>
    public static TaskSelectionResult Resolve(
        ProductionTranslationPlan plan,
        IReadOnlySet<TextCategory> scopeCategories,
        FileSelectionState selection)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(selection);

        var summaries = BuildSummaries(plan);
        var requiresAi = summaries.ToDictionary(
            summary => summary.LogicalFile, summary => summary.RequiresAi, StringComparer.Ordinal);

        var visible = summaries
            .Where(summary => scopeCategories.Count == 0 || scopeCategories.Contains(summary.Category))
            .ToList();
        var visibleFiles = new HashSet<string>(visible.Select(summary => summary.LogicalFile), StringComparer.Ordinal);
        var selectedVisibleFiles = visible
            .Where(summary => selection.IsSelected(summary.LogicalFile))
            .Select(summary => summary.LogicalFile)
            .ToHashSet(StringComparer.Ordinal);

        // ① SelectedNeedTranslate = 范围 ∩ 勾选 ∩ NeedTranslate
        var selectedEntries = plan.NeedTranslate
            .Where(entry => visibleFiles.Contains(entry.Key.RelativeFilePath))
            .Where(entry => selectedVisibleFiles.Contains(entry.Key.RelativeFilePath))
            .ToList();

        // ② 输出条目 = 范围 ∩ （勾选的待翻译文件 ∪ 本就无需 AI 的文件）
        var selectedOutput = plan.OutputEntries
            .Where(entry => visibleFiles.Contains(entry.Key.RelativeFilePath))
            .Where(entry => selectedVisibleFiles.Contains(entry.Key.RelativeFilePath)
                            || !requiresAi.GetValueOrDefault(entry.Key.RelativeFilePath))
            .ToList();

        var unselectedFiles = visible
            .Where(summary => summary.RequiresAi && !selection.IsSelected(summary.LogicalFile))
            .ToList();

        return new TaskSelectionResult(
            SelectedEntries: selectedEntries,
            SelectedOutputEntries: selectedOutput,
            SelectedFiles: selectedVisibleFiles.OrderBy(file => file, StringComparer.Ordinal).ToList(),
            VisibleFiles: visible.Select(summary => summary.LogicalFile).OrderBy(file => file, StringComparer.Ordinal).ToList(),
            VisibleNeedTranslateUnitCount: visible.Sum(summary => summary.NeedTranslateCount),
            UnselectedFileCount: unselectedFiles.Count,
            UnselectedUnitCount: unselectedFiles.Sum(summary => summary.NeedTranslateCount),
            TotalNeedTranslateUnitCount: plan.NeedTranslate.Count,
            TotalFileCount: summaries.Count(summary => summary.RequiresAi));
    }
}
