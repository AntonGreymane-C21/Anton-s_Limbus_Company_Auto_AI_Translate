using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Multilingual;

/// <summary>Canonical Action 计划（第9.0B轮）：韩文 Canonical Diff 决定 TranslationAction。</summary>
public sealed class CanonicalActionPlan
{
    /// <summary>UnitKey → TranslationAction</summary>
    public required IReadOnlyDictionary<string, TranslationAction> Actions { get; init; }

    /// <summary>是否处于「首次建立 Canonical Baseline」的迁移模式</summary>
    public bool IsBaselineMigration { get; init; }

    /// <summary>Inherit 数量</summary>
    public int Inherit { get; init; }

    /// <summary>TranslateMissing 数量</summary>
    public int TranslateMissing { get; init; }

    /// <summary>TranslateNew 数量</summary>
    public int TranslateNew { get; init; }

    /// <summary>TranslateModified 数量</summary>
    public int TranslateModified { get; init; }

    /// <summary>SkipDeleted 数量</summary>
    public int SkipDeleted { get; init; }

    /// <summary>一行式摘要</summary>
    public string Describe() =>
        $"Canonical Actions：[继承 {Inherit}｜缺译 {TranslateMissing}｜新增 {TranslateNew}｜修改 {TranslateModified}｜删除 {SkipDeleted}]" +
        (IsBaselineMigration ? "（首次基线迁移模式）" : string.Empty);
}

/// <summary>
/// Canonical Action 规划器（第9.0B轮）。
///
/// 规则（韩文 Canonical 决定动作）：
///   KR Added    → TranslateNew（首次基线时见下）
///   KR Modified → TranslateModified
///   KR Deleted  → SkipDeleted
///   KR Unchanged→ 旧中文存在 ? Inherit : TranslateMissing
///
/// **首次建立 Canonical Baseline 的迁移模式**（关键安全阀）：
///   此时 previous KR 不存在，Canonical Diff 会把全部单元表现为 Added；
///   若直接映射为 TranslateNew，会把 14 万条推向 API。因此迁移模式下：
///     旧中文存在 → Inherit（最大程度继承既有汉化）；旧中文缺失 → TranslateMissing。
///   **不产生任何 TranslateNew**（也不产生 TranslateModified）。
///
/// EN / JP 参考译本变化（EnglishChanged / JapaneseChanged）**不参与**本规划：
/// 它们只用于诊断 / Trace，不得改变 TranslationAction。
/// </summary>
public static class CanonicalActionPlanner
{
    /// <summary>根据 Canonical Diff 与旧中文可用性生成动作计划。</summary>
    /// <param name="canonical">韩文决定的 Canonical Diff</param>
    /// <param name="oldChineseUnitKeys">旧中文已有的 UnitKey 集合</param>
    /// <param name="isFirstCanonicalBaseline">是否首次建立 KR 基线（迁移模式）</param>
    public static CanonicalActionPlan Plan(
        CanonicalDiffResult canonical,
        IReadOnlyCollection<string>? oldChineseUnitKeys,
        bool isFirstCanonicalBaseline)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        var oldChinese = new HashSet<string>(oldChineseUnitKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
        var actions = new Dictionary<string, TranslationAction>(StringComparer.Ordinal);
        int inherit = 0, missing = 0, added = 0, modified = 0, deleted = 0;

        foreach (var entry in canonical.Entries)
        {
            var key = entry.Key.ToString();
            var hasOldChinese = oldChinese.Contains(key);

            TranslationAction action;
            if (isFirstCanonicalBaseline)
            {
                // 迁移模式：绝不 TranslateNew / TranslateModified
                action = hasOldChinese ? TranslationAction.Inherit : TranslationAction.TranslateMissing;
            }
            else
            {
                action = entry.DiffKind switch
                {
                    DiffKind.Added => TranslationAction.TranslateNew,
                    DiffKind.Modified => TranslationAction.TranslateModified,
                    DiffKind.Deleted => TranslationAction.SkipDeleted,
                    _ => hasOldChinese ? TranslationAction.Inherit : TranslationAction.TranslateMissing,
                };
            }

            actions[key] = action;

            switch (action)
            {
                case TranslationAction.Inherit:
                    inherit++;
                    break;
                case TranslationAction.TranslateMissing:
                    missing++;
                    break;
                case TranslationAction.TranslateNew:
                    added++;
                    break;
                case TranslationAction.TranslateModified:
                    modified++;
                    break;
                case TranslationAction.SkipDeleted:
                    deleted++;
                    break;
            }
        }

        return new CanonicalActionPlan
        {
            Actions = actions,
            IsBaselineMigration = isFirstCanonicalBaseline,
            Inherit = inherit,
            TranslateMissing = missing,
            TranslateNew = added,
            TranslateModified = modified,
            SkipDeleted = deleted,
        };
    }
}