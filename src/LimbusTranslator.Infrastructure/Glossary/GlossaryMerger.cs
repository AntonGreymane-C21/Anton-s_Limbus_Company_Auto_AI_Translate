namespace LimbusTranslator.Infrastructure.Glossary;

/// <summary>术语合并冲突记录（远程与本地同名但译名不同）。</summary>
public sealed class GlossaryConflict
{
    /// <summary>术语</summary>
    public required string Term { get; init; }

    /// <summary>远程译名</summary>
    public required string RemoteTarget { get; init; }

    /// <summary>本地译名</summary>
    public required string LocalTarget { get; init; }

    /// <summary>胜出来源</summary>
    public required GlossaryTermSource Winner { get; init; }

    /// <summary>胜出原因（可读）</summary>
    public required string WinnerReason { get; init; }
}

/// <summary>术语合并结果。</summary>
public sealed class GlossaryMergeResult
{
    /// <summary>合并后的条目（本地优先，未命中本地的远程条目以 Preferred 并入）</summary>
    public required IReadOnlyDictionary<string, GlossaryEntry> Entries { get; init; }

    /// <summary>本地条目数</summary>
    public int LocalCount { get; init; }

    /// <summary>并入的 Paratranz 条目数</summary>
    public int ParatranzCount { get; init; }

    /// <summary>被本地覆盖的远程条目数</summary>
    public int LocalOverrides { get; init; }

    /// <summary>冲突明细（本地胜出）</summary>
    public IReadOnlyList<GlossaryConflict> Conflicts { get; init; } = Array.Empty<GlossaryConflict>();
}

/// <summary>
/// 术语合并器（第8.88轮）。
///
/// 固定优先级（本地人工内容永远优先于远程同步内容）：
///   用户本地 Locked &gt; 用户本地 Preferred &gt; Paratranz 远程
///
/// 规则：
///   - 本地已存在的同名术语：**完全保留**本地 Translation / Locked / Source，远程仅记冲突；
///   - 本地不存在的远程术语：以 <b>Preferred（locked=false）</b> 并入，来源标记为 Paratranz；
///   - 远程术语永远不会变成 Locked（除非将来有可靠的“官方/锁定”语义依据）。
/// </summary>
public static class GlossaryMerger
{
    /// <summary>合并本地术语与 Paratranz 缓存条目。</summary>
    /// <param name="local">本地术语（config/glossary.json）</param>
    /// <param name="remote">远程术语（Paratranz 缓存；可为 null 表示未启用）</param>
    public static GlossaryMergeResult Merge(
        IReadOnlyDictionary<string, GlossaryEntry> local,
        IEnumerable<Paratranz.ParatranzTermEntry>? remote)
    {
        var merged = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in local ?? new Dictionary<string, GlossaryEntry>())
        {
            merged[kv.Key] = kv.Value;
        }

        var conflicts = new List<GlossaryConflict>();
        var paratranzCount = 0;
        var overrides = 0;

        foreach (var entry in remote ?? Enumerable.Empty<Paratranz.ParatranzTermEntry>())
        {
            if (string.IsNullOrWhiteSpace(entry.Term) || string.IsNullOrWhiteSpace(entry.Translation))
            {
                continue;
            }

            var term = entry.Term.Trim();
            var remoteTarget = entry.Translation.Trim();

            if (merged.TryGetValue(term, out var existing))
            {
                if (!string.Equals(existing.Translation, remoteTarget, StringComparison.Ordinal))
                {
                    conflicts.Add(new GlossaryConflict
                    {
                        Term = term,
                        RemoteTarget = remoteTarget,
                        LocalTarget = existing.Translation,
                        Winner = GlossaryTermSource.Local,
                        WinnerReason = existing.Locked ? "本地 Locked 优先" : "本地 Preferred 优先",
                    });
                }

                overrides++;
                continue;
            }

            merged[term] = new GlossaryEntry
            {
                Translation = remoteTarget,
                Locked = false, // 远程术语默认 Preferred
                Source = GlossaryTermSource.Paratranz,
            };
            paratranzCount++;
        }

        return new GlossaryMergeResult
        {
            Entries = merged,
            LocalCount = local?.Count ?? 0,
            ParatranzCount = paratranzCount,
            LocalOverrides = overrides,
            Conflicts = conflicts,
        };
    }
}