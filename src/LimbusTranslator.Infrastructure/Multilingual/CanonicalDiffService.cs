using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Multilingual;

/// <summary>
/// Canonical Diff 服务（第9.0A轮）。
///
/// 核心规则：**只有韩文 KR 决定游戏原文是否变化**（Unchanged / Added / Modified / Deleted）。
/// EN / JP 只是官方译本，其变化**不得**改变 Canonical DiffKind，仅作为诊断记录
/// （<see cref="ReferenceChangeKind"/>），供后续 Prompt（9.0B）使用。
///
/// 复用现有 <see cref="DiffEngine"/>：把「上一份韩文快照」当作旧源、当前韩文单元当作新源。
/// </summary>
public static class CanonicalDiffService
{
    /// <summary>计算 Canonical Diff + 参考译本变化。</summary>
    public static CanonicalDiffResult Compute(
        IReadOnlyList<TranslationUnit>? previousKorean,
        IReadOnlyList<TranslationUnit>? currentKorean,
        IReadOnlyList<TranslationUnit>? previousEnglish = null,
        IReadOnlyList<TranslationUnit>? currentEnglish = null,
        IReadOnlyList<TranslationUnit>? previousJapanese = null,
        IReadOnlyList<TranslationUnit>? currentJapanese = null)
    {
        // 1) Canonical：上一份韩文快照 vs 当前韩文（复用现有 DiffEngine，不新建 Diff 实现）
        var engine = new DiffEngine();
        var canonical = engine.Compute(
            (previousKorean ?? Array.Empty<TranslationUnit>()).ToList(),
            new List<TranslationUnit>(),
            (currentKorean ?? Array.Empty<TranslationUnit>()).ToList());

        var entries = canonical.Entries.ToList();

        return new CanonicalDiffResult
        {
            Entries = entries,
            Added = entries.Count(e => e.DiffKind == DiffKind.Added),
            Modified = entries.Count(e => e.DiffKind == DiffKind.Modified),
            Deleted = entries.Count(e => e.DiffKind == DiffKind.Deleted),
            Unchanged = entries.Count(e => e.DiffKind == DiffKind.Unchanged),
            EnglishChanges = ComputeReferenceChanges(previousEnglish, currentEnglish),
            JapaneseChanges = ComputeReferenceChanges(previousJapanese, currentJapanese),
        };
    }

    /// <summary>比较同一 UnitKey 的参考译本文本（只记录变化项：Changed / MissingBefore / MissingNow）。</summary>
    public static IReadOnlyList<ReferenceChange> ComputeReferenceChanges(
        IReadOnlyList<TranslationUnit>? previous,
        IReadOnlyList<TranslationUnit>? current)
    {
        var before = ToMap(previous);
        var after = ToMap(current);

        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in before.Keys)
        {
            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        foreach (var key in after.Keys)
        {
            if (seen.Add(key))
            {
                keys.Add(key);
            }
        }

        var changes = new List<ReferenceChange>();
        foreach (var key in keys)
        {
            var hasBefore = before.TryGetValue(key, out var oldText);
            var hasAfter = after.TryGetValue(key, out var newText);

            ReferenceChangeKind kind;
            if (!hasBefore && hasAfter)
            {
                kind = ReferenceChangeKind.MissingBefore;
            }
            else if (hasBefore && !hasAfter)
            {
                kind = ReferenceChangeKind.MissingNow;
            }
            else if (!string.Equals(oldText, newText, StringComparison.Ordinal))
            {
                kind = ReferenceChangeKind.Changed;
            }
            else
            {
                continue; // Unchanged 不记录（保持诊断列表精简）
            }

            changes.Add(new ReferenceChange
            {
                UnitKey = key,
                Kind = kind,
                OldText = hasBefore ? oldText : null,
                NewText = hasAfter ? newText : null,
            });
        }

        return changes;
    }

    private static Dictionary<string, string> ToMap(IReadOnlyList<TranslationUnit>? units)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unit in units ?? Array.Empty<TranslationUnit>())
        {
            var key = unit.Key.ToString();
            if (!map.ContainsKey(key))
            {
                map[key] = unit.SourceText ?? string.Empty;
            }
        }

        return map;
    }
}