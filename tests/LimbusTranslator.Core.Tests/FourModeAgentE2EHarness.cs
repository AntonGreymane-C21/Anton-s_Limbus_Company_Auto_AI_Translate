using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;
using LimbusTranslator.Infrastructure.Translation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P2轮（Agent E2E）：Agent 级端到端夹具。
///
/// 目标：在不访问真实 DeepSeek API 的前提下，把**生产链等价**的路径真实跑一遍（第9.0B-P3轮起与 WPF / CLI 同一个生产层）：
///   英文输出结构候选（DiffEngine：旧英文 vs 新英文 + 旧中文）
///   → ProductionTranslationPlanBuilder.Build
///       ① 补入仅存在于韩文树的 Key
///       ② MultilingualSnapshotCapture.ApplyToEntries（真实接线，动作由 Canonical 韩文决定）
///       ③ 按**最终动作**过滤（TranslateNew / TranslateModified / TranslateMissing）
///       ④ 接线后降级为 Inherit 的条目用旧中文物化译文
///   → Coordinator → TranslationAgent.ExecuteAsync
///   → SqliteTranslationMemory（Exact TM）→ request_cache（真实 SQLite 读写）
///   → Fake Provider / Fake Batch Client（只记录与回放，绝不发网络请求）。
///
/// 【生产安全】一切落盘都在本夹具的临时目录内：
///   TmDb / RequestCacheDb / Trace / 三语源树 / Source Snapshot。
///   绝不触碰生产 translation_memory.db、生产 request_cache、生产 source_snapshots 与游戏目录。
///
/// 【顺序事实来源】候选枚举 / 接线 / 过滤**只有一份实现**：ProductionTranslationPlanBuilder
/// （WPF 与 CLI 真实调用同一份；测试禁止另行实现，避免「测试一套、生产另一套」）。
/// </summary>
internal sealed class FourModeAgentE2EHarness : IDisposable
{
    /// <summary>逻辑（规范化）文件名：UnitKey 的 RelativeFilePath 使用该值。</summary>
    public const string LogicalFileName = "Items.json";

    /// <summary>测试用旧中文前缀。</summary>
    public const string OldChinesePrefix = "旧中文";

    private const string ConfigFileName = "field_rules.json";

    /// <summary>临时项目根（三语源树、Snapshot、Trace、日志全部在其中）。</summary>
    public string Root { get; }

    /// <summary>临时 Translation Memory 配置（生产 TM 绝不被使用）。</summary>
    public TranslationMemoryOptions MemoryOptions { get; }

    /// <summary>临时 Request Cache 配置（与 TM 分离的独立临时库）。</summary>
    public TranslationMemoryOptions CacheOptions { get; }

    /// <summary>临时 Translation Memory。</summary>
    public SqliteTranslationMemory Memory { get; }

    /// <summary>临时 request_cache（同样由 SqliteTranslationMemory 承载，但物理文件独立）。</summary>
    public SqliteTranslationMemory Cache { get; }

    /// <summary>夹具自身的调试日志（[调试] 前缀，便于排错）。</summary>
    public List<string> Logs { get; } = new();

    /// <summary>当前写入的三语文本（未写入 → 该语言为 null），供 KR 模式默认按「EN 未变」构造英文输出结构。</summary>
    private readonly Dictionary<SourceLanguage, IReadOnlyList<string>> _currentTexts = new();

    public FourModeAgentE2EHarness()
    {
        Root = Path.Combine(Path.GetTempPath(), "limbus_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);

        MemoryOptions = new TranslationMemoryOptions { DatabasePath = Path.Combine(Root, "tm.db") };
        CacheOptions = new TranslationMemoryOptions { DatabasePath = Path.Combine(Root, "request_cache.db") };

        Memory = new SqliteTranslationMemory(MemoryOptions, Log);
        Cache = new SqliteTranslationMemory(CacheOptions, Log);
    }

    public void Dispose()
    {
        Memory.Dispose();
        Cache.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
        catch
        {
            // 临时目录清理失败可忽略
        }
    }

    private void Log(string message) => Logs.Add(message);

    /// <summary>新版英文目录（生产链的权威结构来源；KR 模式用它解析同级 `kr`）。</summary>
    public string EnglishDirectory => DirectoryFor(SourceLanguage.English);

    /// <summary>当前写入的某语言文本（null = 该语言目录不存在）。</summary>
    public IReadOnlyList<string>? CurrentTextsOf(SourceLanguage language)
        => _currentTexts.TryGetValue(language, out var texts) ? texts : null;

    // ───────────────────────── 三语源树 ─────────────────────────

    /// <summary>第 index 条（0 基）条目的 UnitKey 字符串。</summary>
    public static string KeyOf(int index)
        => $"{LogicalFileName}|{index + 1}|dataList[{index}].name";

    /// <summary>构造 index 条 UnitKey（与三语源树解析结果一致）。</summary>
    public static UnitKey UnitKeyOf(int index) => new()
    {
        RelativeFilePath = LogicalFileName,
        RecordId = (index + 1).ToString(),
        FieldPath = $"dataList[{index}].name",
    };

    /// <summary>某语言的物理目录（Localize/{kr|en|jp}）。</summary>
    public string DirectoryFor(SourceLanguage language)
        => Path.Combine(Root, "Localize", SourceLanguageHelper.GetLocalizeDirectoryName(language));

    /// <summary>写入某语言的文件（覆盖）。</summary>
    public void WriteSources(SourceLanguage language, IReadOnlyList<string> texts)
    {
        var directory = DirectoryFor(language);
        Directory.CreateDirectory(directory);

        var rows = texts
            .Select((text, index) => new Dictionary<string, object?>
            {
                ["id"] = index + 1,
                ["name"] = text,
            })
            .ToList();

        var json = JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["dataList"] = rows },
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(
            Path.Combine(directory, SourceLanguageHelper.GetFilePrefix(language) + LogicalFileName),
            json);
    }

    /// <summary>删除某语言目录（模拟「参考译本缺失 → Fallback 韩文」）。</summary>
    public void RemoveSources(SourceLanguage language)
    {
        var directory = DirectoryFor(language);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>当前存在的语言目录（不存在 → null，与生产 Run 的“缺失目录”语义一致）。</summary>
    public IReadOnlyDictionary<SourceLanguage, string?> LanguageDirectories()
    {
        var map = new Dictionary<SourceLanguage, string?>();
        foreach (var language in SourceLanguageHelper.All)
        {
            var directory = DirectoryFor(language);
            map[language] = Directory.Exists(directory) ? directory : null;
        }

        return map;
    }

    /// <summary>执行一次真实三语捕获（含 Canonical Diff 与 Snapshot 落盘）。</summary>
    public MultilingualCaptureResult Capture()
        => MultilingualSnapshotCapture.Capture(Root, LanguageDirectories(), ConfigDirectory(), Log);

    /// <summary>写入 baseline 三语树并捕获（建立 Previous 快照），随后写入 current 三语树并返回捕获结果。</summary>
    public MultilingualCaptureResult CaptureAfterBaseline(E2ESources baseline, E2ESources current)
    {
        WriteAll(baseline);
        Capture();
        WriteAll(current);
        return Capture();
    }

    /// <summary>按场景写入三语树（null = 该语言目录删除）。</summary>
    public void WriteAll(E2ESources sources)
    {
        WriteOrRemove(SourceLanguage.Korean, sources.Korean);
        WriteOrRemove(SourceLanguage.English, sources.English);
        WriteOrRemove(SourceLanguage.Japanese, sources.Japanese);
    }

    private void WriteOrRemove(SourceLanguage language, IReadOnlyList<string>? texts)
    {
        if (texts is null)
        {
            RemoveSources(language);
            _currentTexts.Remove(language);
            return;
        }

        WriteSources(language, texts);
        _currentTexts[language] = texts;
    }

    /// <summary>定位仓库 config 目录（field_rules.json 所在目录，与既有测试同款解析）。</summary>
    public static string ConfigDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(candidate, ConfigFileName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到 config/" + ConfigFileName);
    }

    // ───────────────────────── 候选条目 ─────────────────────────

    /// <summary>
    /// 英文 Diff 候选（= 生产链的英文输出结构，等价于 DiffWorkflowService.Analyze 的输入/输出语义）。
    ///
    /// 第9.0B-P3轮：夹具不再自己按模式构造候选 —— 候选枚举 / 接线 / 动作过滤全部交给生产层
    /// <see cref="ProductionTranslationPlanBuilder.Build"/>（与 WPF / CLI 完全同源，禁止测试一套、生产另一套）。
    /// </summary>
    public static IReadOnlyList<DiffEntry> BuildEnglishCandidates(
        IReadOnlyList<string> oldEnglish,
        IReadOnlyList<string>? oldChinese,
        IReadOnlyList<string> newEnglish)
        => new DiffEngine()
            .Compute(ToUnits(oldEnglish), ToUnits(oldChinese ?? Array.Empty<string>()), ToUnits(newEnglish))
            .Entries;

    // 第9.0B-P3轮：夹具不再提供「按模式构造候选」的私有实现（旧 BuildCanonicalCandidates / BuildCandidates 已删除），
    // 候选枚举与动作决策统一由 ProductionTranslationPlanBuilder.Build 负责。

    /// <summary>构造 count 条旧中文（key → 旧中文文本）。</summary>
    public static IReadOnlyDictionary<string, string> OldChinese(int count, string prefix = OldChinesePrefix)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            map[KeyOf(index)] = prefix + (index + 1);
        }

        return map;
    }

    /// <summary>
    /// 把「key → 旧中文文本」转成旧中文**单元集合**（等价于工作流 Analyze 产出的
    /// <c>DiffResult.OldChineseUnits</c>：Key = UnitKey，SourceText = 旧中文文本）。
    ///
    /// 第9.0B-P5轮：KR 权威模式按 UnitKey 取旧中文，因此夹具必须提供单元集合（含 KR-only Key），
    /// 而不是只给英文 Diff 用的文本列表。
    /// </summary>
    public static IReadOnlyList<TranslationUnit> BuildOldChineseUnits(IReadOnlyDictionary<string, string>? oldChinese)
    {
        var units = new List<TranslationUnit>();
        if (oldChinese is null)
        {
            return units;
        }

        foreach (var pair in oldChinese)
        {
            var parts = pair.Key.Split('|');
            var filePath = parts.Length > 0 ? parts[0] : string.Empty;
            var recordId = parts.Length > 1 ? parts[1] : string.Empty;
            var fieldPath = parts.Length > 2 ? parts[2] : string.Empty;

            units.Add(new TranslationUnit
            {
                Key = new UnitKey { RelativeFilePath = filePath, RecordId = recordId, FieldPath = fieldPath },
                FilePath = filePath,
                RecordId = recordId,
                FieldPath = fieldPath,
                SourceText = pair.Value,
            });
        }

        return units;
    }

    /// <summary>构造 count 条文案（如 KR / EN / JP 文本）。</summary>
    public static IReadOnlyList<string> Texts(int count, string prefix)
        => Enumerable.Range(0, count).Select(index => $"{prefix}{index + 1}").ToList();

    private static List<TranslationUnit> ToUnits(IReadOnlyList<string> texts)
        => texts.Select((text, index) => new TranslationUnit
        {
            Key = UnitKeyOf(index),
            FilePath = LogicalFileName,
            RecordId = (index + 1).ToString(),
            FieldPath = $"dataList[{index}].name",
            SourceText = text,
            Order = index,
        }).ToList();

    // ───────────────────────── 运行入口（= 生产入口，同一实现） ─────────────────────────

    /// <summary>
    /// 生产入口（与 WPF / CLI **完全同一实现**）：
    /// 英文输出结构 → ProductionTranslationPlanBuilder.Build（接线 → 按最终动作过滤）→ Coordinator → Agent。
    /// </summary>
    /// <param name="mode">翻译模式</param>
    /// <param name="capture">三语捕获结果（null = 未接线，退回纯英文动作语义）</param>
    /// <param name="oldChinese">旧中文（key → 文本；null = 无旧中文）</param>
    /// <param name="providerFactory">Provider 工厂（Fake Provider / DeepSeek + Fake Batch Client）</param>
    /// <param name="oldEnglish">旧英文（默认 = 当前英文树 ⇒ EN 未变）</param>
    /// <param name="newEnglish">当前英文（默认 = 当前英文树）</param>
    /// <param name="glossarySnapshot">第9.0B-P1轮：本次 Run 的术语快照（非 null ⇒ 计划按模式多源匹配并注入 MatchedTerms）</param>
    public async Task<AgentE2ERun> RunProductionChainAsync(
        TranslationMode mode,
        MultilingualCaptureResult? capture,
        IReadOnlyDictionary<string, string>? oldChinese,
        Func<TranslationCacheServices, ITranslationProvider> providerFactory,
        IReadOnlyList<string>? oldEnglish = null,
        IReadOnlyList<string>? newEnglish = null,
        ActiveGlossarySnapshot? glossarySnapshot = null,
        IReadOnlyList<DiffEntry>? entryOverride = null)
    {
        var current = newEnglish ?? CurrentTextsOf(SourceLanguage.English) ?? Array.Empty<string>();
        var entries = BuildEnglishCandidates(
                oldEnglish ?? current,
                oldChinese?.Values.ToList(),
                current)
            .ToList();

        // 唯一生产顺序实现：候选 → ApplyToEntries（Canonical 决定动作）→ 按最终动作过滤
        // 第9.0B-P4轮：同时得到输出结构权威（EN_ONLY → 英文；KR 三模式 → 韩文）与 Expected Key 集
        // 第9.0B-P5轮：把旧中文按 UnitKey 交给计划（KR-only Key 也能正确 Inherit）
        var plan = ProductionTranslationPlanBuilder.Build(
            mode, entries, capture, EnglishDirectory, BuildOldChineseUnits(oldChinese), glossarySnapshot);

        var run = TranslationRunContext.Create();
        var trace = new TranslationTraceWriter(Root, run, Log);
        var services = TranslationCacheServices.Create(Cache, run, trace, Log);
        var provider = providerFactory(services);

        var coordinator = new Coordinator(
            provider, Memory,
            maxConcurrentAgents: 4,
            maxConcurrentApiRequests: 4,
            log: Log,
            cacheServices: services);

        // 第9.0C.3轮：允许调用方传入「本轮任务选择」的结果（未选择时不传任何条目 ⇒ Provider 不会被调用）
        var agentEntries = entryOverride ?? plan.NeedTranslate;
        var result = agentEntries.Count == 0
            ? new CoordinatorResult { Agents = Array.Empty<AgentExecutionResult>() }
            : await coordinator.ExecuteAsync(agentEntries);

        return new AgentE2ERun(mode, plan, capture, result, ReadTrace(trace));
    }

    // ───────────────────────── 观测（TM / Cache / Trace） ─────────────────────────

    /// <summary>清空临时 TM 的 translations（保留 request_cache）：用于验证「第二次命中的是 Cache 而不是 TM」。</summary>
    public void ClearTranslations()
    {
        using var conn = new SqliteConnection($"Data Source={MemoryOptions.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM translations;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>临时 TM 记录数。</summary>
    public int TranslationRowCount() => CountRows(MemoryOptions.DatabasePath, "translations");

    /// <summary>临时 request_cache 记录数。</summary>
    public int RequestCacheRowCount() => CountRows(CacheOptions.DatabasePath, "request_cache");

    /// <summary>临时 request_cache 中的指纹（仅用于隔离性断言）。</summary>
    public IReadOnlyList<string> RequestCacheFingerprints()
    {
        var fingerprints = new List<string>();
        using var conn = new SqliteConnection($"Data Source={CacheOptions.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Fingerprint FROM request_cache ORDER BY Id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            fingerprints.Add(reader.GetString(0));
        }

        return fingerprints;
    }

    private static int CountRows(string databasePath, string table)
    {
        using var conn = new SqliteConnection($"Data Source={databasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 插入一条「历史旧格式」TM 记录（无盐算法，等价第9.0B之前的 EN TM）。
    /// 使用裸 SQL，确保落库的 SourceHash 就是历史算法的字节结果。
    /// </summary>
    public void InsertLegacyTranslationRecord(
        UnitKey key,
        string sourceText,
        string translation,
        TranslationSource source = TranslationSource.AI)
    {
        var hash = SqliteTranslationMemory.ComputeSourceHash(sourceText); // 历史无盐算法
        var now = DateTime.UtcNow.ToString("o");

        using var conn = new SqliteConnection($"Data Source={MemoryOptions.DatabasePath}");
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO translations
                (UnitKey, SourceHash, SourceText, Translation, ContextKey,
                 TranslationSource, NeedsReview, ReviewReason, CreatedAt, UpdatedAt)
            VALUES
                (@key, @hash, @source, @translation, NULL, @sourceType, 0, NULL, @now, @now)
            """;
        cmd.Parameters.AddWithValue("@key", key.ToString());
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.Parameters.AddWithValue("@source", sourceText);
        cmd.Parameters.AddWithValue("@translation", translation);
        cmd.Parameters.AddWithValue("@sourceType", (int)source);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>读取 Trace（JSONL → JsonElement）。</summary>
    public static IReadOnlyList<JsonElement> ReadTrace(TranslationTraceWriter writer)
        => writer.FilePath is not null && File.Exists(writer.FilePath)
            ? File.ReadAllLines(writer.FilePath)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList()
            : Array.Empty<JsonElement>();

    // ==== 夹具（下文追加：Batch 客户端 / 运行结果记录） ====
}

/// <summary>三语源树场景（null = 该语言目录不存在 → Fallback 语义）。</summary>
internal sealed record E2ESources(
    IReadOnlyList<string> Korean,
    IReadOnlyList<string>? English = null,
    IReadOnlyList<string>? Japanese = null);

/// <summary>Provider 实际收到的单条条目快照（拷贝，避免被 Agent 后续修改影响断言）。</summary>
internal sealed record FakeE2EEntrySnapshot(
    string UnitKey,
    string? SourceText,
    string? OldSourceText,
    string? OldTranslation,
    string? CanonicalKorean,
    string? OldCanonicalKorean,
    string? SourceHashSalt,
    TranslationAction Action)
{
    public static FakeE2EEntrySnapshot From(DiffEntry entry) => new(
        entry.Key.ToString(),
        entry.NewSourceText,
        entry.OldSourceText,
        entry.OldTranslation,
        entry.CanonicalKoreanText,
        entry.OldCanonicalKoreanText,
        entry.SourceHashSalt,
        entry.Action);
}

/// <summary>一次 Agent → Provider 调用记录。</summary>
internal sealed record FakeE2ECall(
    TranslationMode Mode,
    string? StageId,
    IReadOnlyList<FakeE2EEntrySnapshot> Entries);

/// <summary>
/// 第9.0B-P2轮：Agent 级 Fake Provider（只记录、只回放，**绝不访问网络**）。
/// 记录 CallCount / 收到条目 / SourceText / CanonicalKorean / Mode Salt / TranslationMode。
/// </summary>
internal sealed class FakeE2EProvider : ITranslationProvider
{
    private readonly List<FakeE2ECall> _calls = new();
    private readonly object _gate = new();

    /// <summary>本次运行锁定的翻译模式（由夹具注入，用于断言「Agent 收到的是哪个模式」）。</summary>
    public TranslationMode Mode { get; set; } = TranslationMode.EnglishOnly;
    /// <summary>假译文工厂（默认纯中文，避免触发英文 / 韩文残留校验）。</summary>
    public Func<FakeE2EEntrySnapshot, string> TranslationFactory { get; set; } = _ => "测试译文";

    /// <summary>Provider 被调用次数（Agent 级 Provider 调用计数）。</summary>
    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    /// <summary>全部调用明细。</summary>
    public IReadOnlyList<FakeE2ECall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    /// <summary>收到的全部条目快照（跨调用展平）。</summary>
    public IReadOnlyList<FakeE2EEntrySnapshot> ReceivedEntries
        => Calls.SelectMany(call => call.Entries).ToList();

    /// <summary>收到的翻译依据文本。</summary>
    public IReadOnlyList<string?> ReceivedSourceTexts
        => ReceivedEntries.Select(e => e.SourceText).ToList();

    /// <summary>收到的韩文原文（Canonical）。</summary>
    public IReadOnlyList<string?> ReceivedCanonicalKorean
        => ReceivedEntries.Select(e => e.CanonicalKorean).ToList();

    /// <summary>收到的旧韩文原文。</summary>
    public IReadOnlyList<string?> ReceivedOldCanonicalKorean
        => ReceivedEntries.Select(e => e.OldCanonicalKorean).ToList();

    /// <summary>收到的 Mode Salt（EN_ONLY 必须为 null）。</summary>
    public IReadOnlyList<string?> ReceivedSourceHashSalts
        => ReceivedEntries.Select(e => e.SourceHashSalt).ToList();

    /// <summary>被调用时的翻译模式序列。</summary>
    public IReadOnlyList<TranslationMode> ReceivedModes
        => Calls.Select(call => call.Mode).ToList();

    public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
        IReadOnlyList<DiffEntry> entries,
        CancellationToken cancellationToken = default,
        string? stageId = null,
        IReadOnlyDictionary<string, TranslationContext>? contexts = null)
    {
        var snapshots = entries.Select(FakeE2EEntrySnapshot.From).ToList();
        lock (_gate)
        {
            _calls.Add(new FakeE2ECall(Mode, stageId, snapshots));
        }

        var results = new Dictionary<string, TranslationResult>(StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            results[snapshot.UnitKey] = new TranslationResult
            {
                Key = ParseKey(snapshot.UnitKey),
                Translation = TranslationFactory(snapshot),
                Source = TranslationSource.AI,
                NeedsReview = false,
            };
        }

        return Task.FromResult<IReadOnlyDictionary<string, TranslationResult>>(results);
    }

    /// <summary>UnitKey 字符串 → UnitKey（与生产链解析方式一致）。</summary>
    internal static UnitKey ParseKey(string unitKey)
    {
        var parts = unitKey.Split('|');
        return new UnitKey
        {
            RelativeFilePath = parts.Length > 0 ? parts[0] : string.Empty,
            RecordId = parts.Length > 1 ? parts[1] : string.Empty,
            FieldPath = parts.Length > 2 ? parts[2] : string.Empty,
        };
    }
}

/// <summary>
/// 第9.0B-P2轮：Batch 级 Fake 客户端（Provider 真实调用它，但**不发生任何网络请求**）。
/// 记录 BatchCallCount / TotalItemCount / 每条请求项（含韩文原文与翻译模式）。
/// </summary>
internal sealed class FakeE2EBatchClient : IDeepSeekBatchClient
{
    private readonly List<DeepSeekTranslateRequestItem> _allItems = new();
    private readonly List<int> _itemCounts = new();
    private readonly List<TranslationMode> _modes = new();
    private readonly List<DeepSeekRequestThinking?> _thinkings = new();
    private readonly List<string> _glossaryPrompts = new();
    private readonly object _gate = new();

    /// <summary>假译文工厂（默认纯中文）。</summary>
    public Func<DeepSeekTranslateRequestItem, string> TranslationFactory { get; set; } = _ => "测试译文";

    /// <summary>
    /// 第9.0C.2轮：按**调用序号**脚本化的假译文（index 从 0 开始）。
    /// 用于让「首次翻译」与「锁定术语修正」两次请求返回不同内容。
    /// </summary>
    public Func<int, DeepSeekTranslateRequestItem, string>? ScriptedTranslation { get; set; }

    /// <summary>锁定术语修正请求收到的条目（第9.0C.2轮）。</summary>
    public IReadOnlyList<DeepSeekTranslateRequestItem> RepairItems
        => AllItems.Where(item => !string.IsNullOrEmpty(item.LockedTerms)).ToList();

    /// <summary>常规翻译请求收到的条目（第9.0C.2轮）。</summary>
    public IReadOnlyList<DeepSeekTranslateRequestItem> TranslateItems
        => AllItems.Where(item => string.IsNullOrEmpty(item.LockedTerms)).ToList();

    /// <summary>Batch 调用次数（= 真实网络请求次数，本测试恒为 0 才是安全的）。</summary>
    public int BatchCallCount
    {
        get
        {
            lock (_gate)
            {
                return _itemCounts.Count;
            }
        }
    }

    /// <summary>累计条目数。</summary>
    public int TotalItemCount
    {
        get
        {
            lock (_gate)
            {
                return _itemCounts.Sum();
            }
        }
    }

    /// <summary>每次调用的条目数。</summary>
    public IReadOnlyList<int> BatchItemCounts
    {
        get
        {
            lock (_gate)
            {
                return _itemCounts.ToArray();
            }
        }
    }

    /// <summary>每次调用携带的翻译模式。</summary>
    public IReadOnlyList<TranslationMode> Modes
    {
        get
        {
            lock (_gate)
            {
                return _modes.ToArray();
            }
        }
    }

    /// <summary>累计收到的请求项。</summary>
    public IReadOnlyList<DeepSeekTranslateRequestItem> AllItems
    {
        get
        {
            lock (_gate)
            {
                return _allItems.ToArray();
            }
        }
    }

    /// <summary>累计收到的 SourceText。</summary>
    public IReadOnlyList<string> Sources => AllItems.Select(item => item.Source).ToList();

    /// <summary>累计收到的韩文原文（EN_ONLY 必须为空）。</summary>
    public IReadOnlyList<string?> CanonicalKoreans => AllItems.Select(item => item.CanonicalKorean).ToList();

    /// <summary>每次调用携带的 Thinking 设置（第9.0B-P1轮：验证最终请求真的开启思考）。</summary>
    public IReadOnlyList<DeepSeekRequestThinking?> Thinkings
    {
        get
        {
            lock (_gate)
            {
                return _thinkings.ToArray();
            }
        }
    }

    /// <summary>每次调用携带的术语提示词（第9.0B-P1轮：验证 Prompt 真的注入了共享术语）。</summary>
    public IReadOnlyList<string> GlossaryPrompts
    {
        get
        {
            lock (_gate)
            {
                return _glossaryPrompts.ToArray();
            }
        }
    }

    public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt)
        => Task.FromResult(Record(items, TranslationMode.EnglishOnly, thinking: null, glossaryPrompt: glossaryPrompt));

    public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt,
        DeepSeekRequestThinking thinking,
        bool includeModifiedRule,
        TextCategory? category,
        TranslationMode translationMode)
        => Task.FromResult(Record(items, translationMode, thinking, glossaryPrompt));

    private DeepSeekBatchResult Record(
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        TranslationMode mode,
        DeepSeekRequestThinking? thinking,
        string? glossaryPrompt = null)
    {
        int callIndex;
        lock (_gate)
        {
            callIndex = _itemCounts.Count;
            _itemCounts.Add(items.Count);
            _modes.Add(mode);
            _thinkings.Add(thinking);
            _glossaryPrompts.Add(glossaryPrompt ?? string.Empty);
            _allItems.AddRange(items);
        }

        string FakeTranslation(DeepSeekTranslateRequestItem item)
            => ScriptedTranslation is null ? TranslationFactory(item) : ScriptedTranslation(callIndex, item);

        return new DeepSeekBatchResult
        {
            Items = items.ToDictionary(
                item => item.Id,
                item => new DeepSeekTranslateItem(item.Id, FakeTranslation(item), NeedsReview: false, Reason: string.Empty),
                StringComparer.Ordinal),
            ResponseId = "fake-e2e-response",
            ResponseModel = "fake-e2e-model",
            PromptTokens = 1,
            CompletionTokens = 1,
            TotalTokens = 2,
            RetryCount = 0,
            DurationMs = 1,
        };
    }
}

/// <summary>一次 Agent 级 E2E 运行的完整观测结果。</summary>
internal sealed record AgentE2ERun(
    TranslationMode Mode,
    ProductionTranslationPlan Plan,
    MultilingualCaptureResult? Capture,
    CoordinatorResult Coordinator,
    IReadOnlyList<JsonElement> Trace)
{
    /// <summary>接线后的候选条目（来自生产计划）。</summary>
    public IReadOnlyList<DiffEntry> Candidates => Plan.Candidates;

    /// <summary>真正进入 Agent 的条目（接线后按最终动作过滤的结果，来自生产计划）。</summary>
    public IReadOnlyList<DiffEntry> AgentEntries => Plan.NeedTranslate;

    /// <summary>被接线覆盖动作的条目数。</summary>
    public int PatchedCount => Plan.PatchedCount;

    /// <summary>接线后转继承（用旧中文补齐译文）的条目数。</summary>
    public int InheritedKeptCount => Plan.InheritedKeptCount;

    /// <summary>仅存在于韩文树、不在英文输出结构里的候选条目数。</summary>
    public int KoreanOnlyCount => Plan.KoreanOnlyCount;

    /// <summary>本次是否成功获得 Canonical 捕获（未接线时为 false）。</summary>
    public bool HasCanonicalCapture => Plan.HasCanonicalCapture;

    /// <summary>接线后某个动作的条目数。</summary>
    public int Count(TranslationAction action) => Candidates.Count(e => e.Action == action);

    public int Inherit => Count(TranslationAction.Inherit);
    public int TranslateNew => Count(TranslationAction.TranslateNew);
    public int TranslateModified => Count(TranslationAction.TranslateModified);
    public int TranslateMissing => Count(TranslationAction.TranslateMissing);
    public int SkipDeleted => Count(TranslationAction.SkipDeleted);

    /// <summary>真正进入 Agent 的 UnitKey。</summary>
    public IReadOnlyList<string> AgentUnitKeys => AgentEntries.Select(e => e.Key.ToString()).ToList();

    /// <summary>Trace 中 cacheHit = true 的请求数。</summary>
    public int TraceCacheHits => Trace.Count(t => t.TryGetProperty("cacheHit", out var value) && value.GetBoolean());

    /// <summary>Trace 中 networkCalled = true 的请求数。</summary>
    public int TraceNetworkCalls => Trace.Count(t => t.TryGetProperty("networkCalled", out var value) && value.GetBoolean());

    /// <summary>Trace 中出现的请求指纹（去重）。</summary>
    public IReadOnlyList<string> TraceFingerprints => Trace
        .Select(t => t.TryGetProperty("fingerprint", out var value) ? value.GetString() : null)
        .Where(fingerprint => !string.IsNullOrEmpty(fingerprint))
        .Select(fingerprint => fingerprint!)
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
