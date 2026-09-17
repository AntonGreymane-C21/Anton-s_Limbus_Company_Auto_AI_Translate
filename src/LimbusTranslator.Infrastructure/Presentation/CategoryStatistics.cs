using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>分类统计行（第9.0C.23轮：GUI 与测试共用的唯一实现）。</summary>
public sealed record CategoryStatRow(
    TextCategory Category,
    string DisplayName,
    int TotalFileCount,
    int NeedTranslateFileCount,
    int ReferenceOnlyFileCount)
{
    /// <summary>UI 用的"仅参考变化"提示（无则为空串）。</summary>
    public string ReferenceOnlyNote =>
        ReferenceOnlyFileCount > 0 ? $"｜仅参考变化 {ReferenceOnlyFileCount}" : string.Empty;
}

/// <summary>
/// 分类统计（第9.0C.23轮）。
///
/// 【为什么需要这个类】真实反馈："播报员 需译 1，但「需要处理的文件」里没有这个文件"。
/// 根因：旧实现用**文件级英文 Diff**（<c>Kind != Unchanged</c>）统计"需译"，而文件列表来自
/// **生产计划**的 <c>NeedTranslate</c>（KR 权威模式下：韩文未变 ⇒ 继承 ⇒ 根本不需要 AI）
/// ⇒ 两个数字口径不同，界面自相矛盾。
///
/// 现在统一为："统计与文件列表**同源**"（都来自生产计划）：
///   - <see cref="CategoryStatRow.TotalFileCount"/>：权威输出结构里的逻辑文件数；
///   - <see cref="CategoryStatRow.NeedTranslateFileCount"/>：计划里"需要 AI"的逻辑文件数（= 文件列表口径）；
///   - <see cref="CategoryStatRow.ReferenceOnlyFileCount"/>：文件级 Diff 有变化、但计划判定**不需要 AI**
///     的文件数（即"仅英文 / 日文参考变化"）—— 用来向用户解释"它为什么没出现在需要处理的文件里"。
/// </summary>
public static class CategoryStatistics
{
    /// <summary>
    /// 构造分类统计。
    /// </summary>
    /// <param name="plan">生产翻译计划（权威结构与动作的唯一事实来源）</param>
    /// <param name="fileKinds">
    /// 文件级 Diff 类型（逻辑相对路径 → <see cref="FileDiffKind"/>；可为 null）。
    /// 仅用于计算"仅参考变化"的文件数。
    /// </param>
    public static IReadOnlyList<CategoryStatRow> Build(
        ProductionTranslationPlan plan,
        IReadOnlyDictionary<string, FileDiffKind>? fileKinds = null)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var kinds = fileKinds ?? new Dictionary<string, FileDiffKind>(StringComparer.OrdinalIgnoreCase);

        var filesByCategory = plan.OutputEntries
            .GroupBy(entry => TextCategoryHelper.FromRelativePath(entry.Key.RelativeFilePath))
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.Key.RelativeFilePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var needFilesByCategory = plan.NeedTranslate
            .GroupBy(entry => TextCategoryHelper.FromRelativePath(entry.Key.RelativeFilePath))
            .ToDictionary(
                group => group.Key,
                group => new HashSet<string>(
                    group.Select(entry => entry.Key.RelativeFilePath), StringComparer.OrdinalIgnoreCase));

        var rows = new List<CategoryStatRow>();
        foreach (var category in Enum.GetValues<TextCategory>())
        {
            var files = filesByCategory.TryGetValue(category, out var list) ? list : new List<string>();
            var needFiles = needFilesByCategory.TryGetValue(category, out var need) ? need : new HashSet<string>();

            var referenceOnly = files.Count(file =>
                !needFiles.Contains(file)
                && kinds.TryGetValue(file, out var kind)
                && kind != FileDiffKind.Unchanged);

            rows.Add(new CategoryStatRow(
                category,
                TextCategoryHelper.GetDisplayName(category),
                files.Count,
                needFiles.Count,
                referenceOnly));
        }

        return rows;
    }
}
