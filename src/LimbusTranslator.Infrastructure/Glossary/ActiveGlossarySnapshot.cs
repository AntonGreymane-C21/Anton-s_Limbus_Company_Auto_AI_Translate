namespace LimbusTranslator.Infrastructure.Glossary;

/// <summary>
/// 一次翻译运行使用的**术语快照**（第8.875轮）。
///
/// 目的：此前 Provider（Prompt 注入）与 Validator 各自 <c>new GlossaryService()</c>，
/// 同一运行内可能出现「Prompt 用 A 版本、校验用 B 版本」的分叉。
/// 现在改为：**运行开始 → 创建一次快照 → 本次运行全流程共享**，使
///   一次运行 = 一个确定的术语版本
/// 语义明确：
///   - 运行中即使用户在 GUI 修改 config/glossary.json，当前运行**不受影响**；
///   - 下一次运行会读取新版本（新快照）。
///
/// 快照本身不可变（构造时深拷贝为只读字典），运行中不存在任何修改入口。
/// </summary>
public sealed class ActiveGlossarySnapshot
{
    private readonly Dictionary<string, GlossaryEntry> _entries;

    private ActiveGlossarySnapshot(
        Dictionary<string, GlossaryEntry> entries,
        string? sourcePath,
        DateTime createdAtUtc,
        string snapshotHash)
    {
        _entries = entries;
        SourcePath = sourcePath;
        CreatedAtUtc = createdAtUtc;
        SnapshotHash = snapshotHash;
    }

    /// <summary>空快照（无术语；用于测试或术语库缺失）。</summary>
    public static ActiveGlossarySnapshot Empty { get; } =
        new(new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase), null, DateTime.UtcNow, ComputeHash(new Dictionary<string, GlossaryEntry>()));

    /// <summary>术语条目（只读；运行中不可修改）。</summary>
    public IReadOnlyDictionary<string, GlossaryEntry> Entries => _entries;

    /// <summary>术语数量。</summary>
    public int Count => _entries.Count;

    /// <summary>术语库文件路径（可能为 null）。</summary>
    public string? SourcePath { get; }

    /// <summary>快照创建时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; }

    /// <summary>
    /// 快照内容 Hash（canonical：按 Key 稳定排序 + 译名 + locked，SHA256 前 16 位）。
    ///
    /// 用途：**运行诊断 / Trace 记录**（`glossarySnapshotHash`）。
    /// 注意：它**不进入请求指纹** —— 请求指纹仍由「本请求实际命中的术语子集」决定
    /// （`GlossarySubsetHash`），否则未命中的无关术语变化会导致所有请求缓存失效。
    /// </summary>
    public string SnapshotHash { get; }

    /// <summary>
    /// 从 config 目录加载快照（只读一次，之后不再访问磁盘）。
    /// </summary>
    /// <param name="configDir">配置目录</param>
    public static ActiveGlossarySnapshot Load(string? configDir)
    {
        var service = new GlossaryService(configDir);
        return FromEntries(service.Entries, service.SourcePath);
    }

    /// <summary>
    /// 第8.88轮：加载本地术语 + （若启用）已同步的 Paratranz 缓存并合并。
    ///
    /// 优先级固定：本地 Locked &gt; 本地 Preferred &gt; Paratranz（远程术语一律作为 Preferred 并入）。
    /// 远程缓存不可用/未启用时，等价于只加载本地术语。
    /// </summary>
    /// <param name="configDir">配置目录</param>
    /// <param name="paratranz">Paratranz 配置（null 或未启用表示不合并远程）</param>
    /// <param name="mergeSummary">合并摘要（供日志/GUI 展示）</param>
    public static ActiveGlossarySnapshot LoadWithParatranz(
        string? configDir,
        Paratranz.ParatranzOptions? paratranz,
        out GlossaryMergeSummary mergeSummary)
    {
        var service = new GlossaryService(configDir);
        IReadOnlyList<Paratranz.ParatranzTermEntry>? remote = null;

        if (paratranz is { Enabled: true })
        {
            var cachePath = ResolveCachePath(configDir, paratranz.CachePath);
            var cache = Paratranz.ParatranzGlossaryCacheStore.TryLoad(cachePath);
            remote = cache?.Entries;
            mergeSummary = new GlossaryMergeSummary
            {
                ParatranzEnabled = true,
                CachePath = cachePath,
                CacheFound = cache is not null,
                FetchedAtUtc = cache?.FetchedAtUtc,
                RemoteCount = cache?.Entries.Count ?? 0,
            };
        }
        else
        {
            mergeSummary = new GlossaryMergeSummary();
        }

        var merged = GlossaryMerger.Merge(service.Entries, remote);
        mergeSummary.LocalCount = merged.LocalCount;
        mergeSummary.MergedParatranzCount = merged.ParatranzCount;
        mergeSummary.LocalOverrides = merged.LocalOverrides;
        mergeSummary.Conflicts = merged.Conflicts;

        var snapshot = FromEntries(merged.Entries, service.SourcePath);
        return snapshot;
    }

    /// <summary>解析 Paratranz 缓存路径（相对路径按项目根解析）。</summary>
    private static string ResolveCachePath(string? configDir, string cachePath)
    {
        if (Path.IsPathRooted(cachePath))
        {
            return cachePath;
        }

        var root = configDir is not null && Directory.Exists(configDir)
            ? new DirectoryInfo(Path.GetFullPath(Path.Combine(configDir, ".."))).FullName
            : AppContext.BaseDirectory;
        return Path.Combine(root, cachePath);
    }

    /// <summary>由既有条目集合创建快照（内部深拷贝，保证不可变）。</summary>
    public static ActiveGlossarySnapshot FromEntries(
        IEnumerable<KeyValuePair<string, GlossaryEntry>> entries,
        string? sourcePath = null)
    {
        var copy = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in entries)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
            {
                continue;
            }

            copy[kv.Key] = new GlossaryEntry
            {
                Translation = kv.Value?.Translation ?? string.Empty,
                Locked = kv.Value?.Locked ?? false,
                // 第8.89轮：来源（Local / Paratranz）必须随快照保留，否则诊断与优先级判断会失真
                Source = kv.Value?.Source ?? GlossaryTermSource.Local,
            };
        }

        return new ActiveGlossarySnapshot(copy, sourcePath, DateTime.UtcNow, ComputeHash(copy));
    }

    /// <summary>
    /// 术语匹配（词边界 + 重叠消解 Longest Match Wins；与 Validator 共用同一实现）。
    /// 返回最终有效术语（已抑制被更长术语遮蔽的短术语），按术语 Key 稳定排序。
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, GlossaryEntry>> SelectTerms(IEnumerable<string> texts)
        => SelectTermsFrom(_entries, texts);

    /// <summary>
    /// 共享的术语选择实现（GlossaryService / Snapshot 共用）：
    ///   1. 对每条文本找出候选命中（词边界）；
    ///   2. 重叠消解（更长、更具体的术语优先）；
    ///   3. 跨文本求并集（按 Key 稳定排序）。
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, GlossaryEntry>> SelectTermsFrom(
        IReadOnlyDictionary<string, GlossaryEntry> entries,
        IEnumerable<string> texts)
    {
        if (entries is null || entries.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, GlossaryEntry>>();
        }

        var textList = texts?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
        if (textList.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, GlossaryEntry>>();
        }

        var terms = entries.Select(kv => (Term: kv.Key, Translation: kv.Value.Translation, Locked: kv.Value.Locked)).ToList();
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var text in textList)
        {
            foreach (var match in Core.Text.TermMatcher.FindEffectiveMatches(text!, terms))
            {
                selected.Add(match.Term);
            }
        }

        return selected
            .Select(term => new KeyValuePair<string, GlossaryEntry>(term, entries[term]))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>canonical 内容 Hash（稳定排序；语义相同则 Hash 相同）。</summary>
    private static string ComputeHash(Dictionary<string, GlossaryEntry> entries)
    {
        var canonical = string.Join(
            '|',
            entries
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => $"{kv.Key}={kv.Value.Translation}({(kv.Value.Locked ? "L" : "U")})"));
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes)[..16];
    }
}

/// <summary>术语合并摘要（第8.88轮；日志与 GUI 诊断用）。</summary>
public sealed class GlossaryMergeSummary
{
    /// <summary>是否启用 Paratranz</summary>
    public bool ParatranzEnabled { get; set; }

    /// <summary>缓存路径</summary>
    public string? CachePath { get; set; }

    /// <summary>是否找到缓存文件</summary>
    public bool CacheFound { get; set; }

    /// <summary>缓存抓取时间</summary>
    public DateTime? FetchedAtUtc { get; set; }

    /// <summary>缓存中的远程条目数</summary>
    public int RemoteCount { get; set; }

    /// <summary>本地条目数</summary>
    public int LocalCount { get; set; }

    /// <summary>实际并入的远程条目数</summary>
    public int MergedParatranzCount { get; set; }

    /// <summary>被本地覆盖（同名）的远程条目数</summary>
    public int LocalOverrides { get; set; }

    /// <summary>冲突明细</summary>
    public IReadOnlyList<GlossaryConflict> Conflicts { get; set; } = Array.Empty<GlossaryConflict>();

    /// <summary>一行式摘要（GUI/日志）</summary>
    public string Describe() => ParatranzEnabled
        ? $"Paratranz：启用｜缓存={(CacheFound ? "有" : "无")}｜远程条目={RemoteCount}｜并入={MergedParatranzCount}｜本地覆盖={LocalOverrides}｜冲突={Conflicts.Count}"
        : "Paratranz：未启用";
}