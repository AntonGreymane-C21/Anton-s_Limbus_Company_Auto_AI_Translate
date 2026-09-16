using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Cli;

/// <summary>单个运行阶段的期望（决定硬性断言：是否允许出网 / 必须命中什么）。</summary>
internal enum StageExpectation
{
    /// <summary>Stage1：真实网络（每个模式子根 1 次请求 / 2 条单元）。</summary>
    Network,

    /// <summary>Stage2：TM Replay（新增网络必须为 0，必须 ExactUnit 命中）。</summary>
    TmReplay,

    /// <summary>Stage3：RequestCache Replay（新增网络必须为 0，必须 Cache Hit）。</summary>
    CacheReplay,
}

/// <summary>一个模式子根（2 条单元 + 自己的三语树与 TEMP 数据目录）。</summary>
internal sealed class SmokePhase
{
    public required string Name { get; init; }
    public required TranslationMode Mode { get; init; }
    public required string Root { get; init; }
    public required string NewEnglishDirectory { get; init; }
    public required string OldEnglishDirectory { get; init; }
    public required string OldChineseDirectory { get; init; }
    public required string DatabasePath { get; init; }
    public required IReadOnlyList<SmokeUnitExpectation> Units { get; init; }
    public required MultilingualCaptureResult Capture { get; init; }

    /// <summary>
    /// 期望写入 output 的条目数（默认 = <see cref="Units"/>.Count）。
    /// 第9.0B 最终 Smoke 的样本文件里包含「已继承的邻接记录」⇒ 输出条目数会多于待翻译单元数。
    /// </summary>
    public int ExpectedOutputEntryCount { get; init; }
}

/// <summary>Smoke 会话（TEMP 根 + 配置 + 预算 + 阶段记录）。</summary>
internal sealed class SmokeSession
{
    public SmokeSession(
        string configDir,
        string tempRoot,
        DeepSeekOptions options,
        BatchOptions batch,
        ActiveGlossarySnapshot glossarySnapshot,
        SmokeBudget budget)
    {
        ConfigDir = configDir;
        TempRoot = tempRoot;
        Options = options;
        Batch = batch;
        GlossarySnapshot = glossarySnapshot;
        Budget = budget;
    }

    public string ConfigDir { get; }
    public string TempRoot { get; }
    public DeepSeekOptions Options { get; }
    public BatchOptions Batch { get; }
    public ActiveGlossarySnapshot GlossarySnapshot { get; }
    public SmokeBudget Budget { get; }
    public List<string> Violations { get; } = new();
    public List<string> Notes { get; } = new();

    /// <summary>调试日志（同时打印到控制台并留档，便于报告引用；内容不含任何凭据）。</summary>
    public void Log(string message)
    {
        Console.WriteLine(message);
        Notes.Add(message);
    }

    /// <summary>清空 TEMP TM 的 translations（只作用于 TEMP；request_cache 保留）。</summary>
    public int ClearTemporaryTranslations()
    {
        var cleared = 0;
        foreach (var dbPath in Directory.EnumerateFiles(Path.Combine(TempRoot), "translation_memory.db", SearchOption.AllDirectories))
        {
            using var connection = new SqliteConnection($"Data Source={dbPath.Replace("\\", "/")}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM translations;";
            cleared += command.ExecuteNonQuery();
        }

        return cleared;
    }

    /// <summary>汇总三个阶段：跨阶段一致性 + 硬上限复核 + TEMP TM/缓存证据 + 汇总 JSON。</summary>
    public int Finish(
        IReadOnlyList<SmokePhase> phases,
        IReadOnlyList<SmokeStageResult> stage1,
        IReadOnlyList<SmokeStageResult> stage2,
        IReadOnlyList<SmokeStageResult> stage3)
    {
        // ① 跨阶段一致性：Stage2 / Stage3 的译文必须与 Stage1（真实 DeepSeek 返回）逐条一致
        for (var index = 0; index < phases.Count; index++)
        {
            var phaseName = phases[index].Name;
            foreach (var (stageLabel, stageResult) in new[]
                     {
                         ("stage2_tm_replay", stage2[index]),
                         ("stage3_cache_replay", stage3[index]),
                     })
            {
                foreach (var pair in stage1[index].Translations)
                {
                    if (!stageResult.Translations.TryGetValue(pair.Key, out var value))
                    {
                        Violations.Add($"跨阶段一致性：{phaseName} {stageLabel} 缺少 {pair.Key} 的译文");
                    }
                    else if (!string.Equals(pair.Value, value, StringComparison.Ordinal))
                    {
                        Violations.Add($"跨阶段一致性：{phaseName} {stageLabel} 的 {pair.Key} 译文与 Stage1 不一致");
                    }
                }
            }
        }

        // ② 硬上限复核（代码层保护之外的最后一道核对）
        if (Budget.Units > SmokeBudget.MaxTranslationUnits)
        {
            Violations.Add($"真实翻译单元 {Budget.Units} 超过上限 {SmokeBudget.MaxTranslationUnits}");
        }

        if (Budget.Requests > SmokeBudget.MaxNetworkRequests)
        {
            Violations.Add($"真实网络请求 {Budget.Requests} 超过上限 {SmokeBudget.MaxNetworkRequests}");
        }

        if (Budget.ObservedRetryCount != 0)
        {
            Violations.Add($"观测到客户端重试 {Budget.ObservedRetryCount} 次（重试同样计入上限；本轮要求 0）");
        }

        // ③ TEMP TM / request_cache 证据
        var tmEvidence = VerifyTemporaryMemory(phases, stage1);

        // ④ 汇总 JSON + 控制台摘要
        var summary = new
        {
            Round = "9.0B-P6",
            Stage = "real-deepseek-8-unit-e2e-smoke",
            TempRoot,
            CredentialDetected = true,
            Provider = DeepSeekTranslationProvider.ProviderName,
            Model = Options.Model,
            BaseUrlHost = new Uri(Options.ApiUrl).Host,
            MaxRetryForced = Options.MaxRetry,
            HardLimits = new { MaxTranslationUnits = SmokeBudget.MaxTranslationUnits, MaxNetworkRequests = SmokeBudget.MaxNetworkRequests },
            Budget = new { Budget.Units, Budget.Requests, Budget.ObservedRetryCount },
            Stage1 = stage1,
            Stage2 = stage2,
            Stage3 = stage3,
            TemporaryMemory = tmEvidence,
            Violations,
            Notes,
            Passed = Violations.Count == 0,
        };
        var summaryPath = Path.Combine(TempRoot, "smoke_summary.json");
        SmokeReportWriter.Save(summaryPath, summary);

        Log("[调试] ===== 阶段汇总 =====");
        foreach (var (label, stage) in new[]
                 {
                     ("Stage1 真实网络", stage1),
                     ("Stage2 TM Replay", stage2),
                     ("Stage3 Cache Replay", stage3),
                 })
        {
            Log($"[调试] {label}：单元 +{stage.Sum(r => r.UnitDelta)}｜网络请求 +{stage.Sum(r => r.RequestDelta)}"
                + $"｜Trace 网络行 {stage.Sum(r => r.TraceNetworkCalledCount)}／缓存命中行 {stage.Sum(r => r.TraceCacheHitCount)}"
                + $"｜TM 命中 {stage.Sum(r => r.TmHitCount)}｜Merge 写入 {stage.Sum(r => r.MergeWrittenEntryCount)}"
                + $"｜Gate Missing {stage.Sum(r => r.GateMissingExpectedKeyCount)}／Unexpected {stage.Sum(r => r.GateUnexpectedOutputKeyCount)}");
        }

        Log($"[调试] TEMP TM：{tmEvidence.TranslationRowCount} 行｜request_cache：{tmEvidence.RequestCacheRowCount} 行"
            + $"｜盐隔离检查通过 {tmEvidence.SaltChecksPassed}/{(tmEvidence.SaltChecksPassed + tmEvidence.SaltChecksFailed)}");
        Log($"[调试] {Budget.Describe()}");
        Log($"[调试] 汇总 JSON: {summaryPath}");

        if (Violations.Count > 0)
        {
            Log($"[错误] 真实 API Smoke 存在 {Violations.Count} 项违规：");
            foreach (var violation in Violations.Take(30))
            {
                Log($"[错误]   {violation}");
            }

            Log("[错误] ===== 第9.0B-P6 真实 DeepSeek 8 条 E2E Smoke：FAILED =====");
            return 1;
        }

        Log("[调试] ===== 第9.0B-P6 真实 DeepSeek 8 条 E2E Smoke：PASSED =====");
        return 0;
    }

    /// <summary>
    /// 校验 TEMP TM 记录（只读 TEMP 库）：EN_ONLY 必须无盐、KR 模式必须带盐（哈希必须等于
    /// <c>ComputeSourceHash(SelectedText, Salt)</c>），并统计 request_cache 与指纹前缀。
    /// </summary>
    private SmokeMemoryEvidence VerifyTemporaryMemory(
        IReadOnlyList<SmokePhase> phases,
        IReadOnlyList<SmokeStageResult> stage1)
    {
        var rows = 0;
        var cacheRows = 0;
        var passed = 0;
        var failed = 0;
        var fingerprints = new List<string>();

        for (var index = 0; index < phases.Count; index++)
        {
            var phase = phases[index];
            using var connection = new SqliteConnection($"Data Source={phase.DatabasePath.Replace("\\", "/")}");
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT UnitKey, SourceHash, SourceText, Translation FROM translations;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    rows++;
                    var unitKey = reader.GetString(0);
                    var hash = reader.GetString(1);
                    var sourceText = reader.GetString(2);
                    var translation = reader.GetString(3);
                    var snapshot = stage1[index].Units.FirstOrDefault(unit => unit.UnitKey == unitKey);
                    var salt = snapshot?.SourceHashSalt;
                    var expectedHash = SqliteTranslationMemory.ComputeSourceHash(sourceText, salt);

                    if (string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(translation))
                    {
                        passed++;
                    }
                    else
                    {
                        failed++;
                        Violations.Add(
                            $"TEMP TM 哈希/盐校验失败：{phase.Name} {unitKey}（salt={(salt is null ? "null" : "非空")}）");
                    }
                }
            }

            using (var cacheCommand = connection.CreateCommand())
            {
                cacheCommand.CommandText = "SELECT Fingerprint FROM request_cache;";
                using var cacheReader = cacheCommand.ExecuteReader();
                while (cacheReader.Read())
                {
                    cacheRows++;
                    fingerprints.Add(cacheReader.GetString(0));
                }
            }
        }

        var expectedRows = stage1.Sum(stage => stage.Units.Count);
        if (rows != expectedRows)
        {
            Violations.Add($"TEMP TM 行数 {rows} != 真实翻译单元数 {expectedRows}");
        }

        var expectedCacheRows = stage1.Sum(stage => stage.RequestDelta);
        if (cacheRows != expectedCacheRows)
        {
            Violations.Add($"TEMP request_cache 行数 {cacheRows} != Stage1 真实请求数 {expectedCacheRows}");
        }

        if (fingerprints.Any(fingerprint => !fingerprint.StartsWith("v3:", StringComparison.Ordinal)))
        {
            Violations.Add("TEMP request_cache 存在非 v3 指纹（Fingerprint v3 未生效）");
        }

        return new SmokeMemoryEvidence
        {
            TranslationRowCount = rows,
            RequestCacheRowCount = cacheRows,
            SaltChecksPassed = passed,
            SaltChecksFailed = failed,
            Fingerprints = fingerprints,
        };
    }
}

/// <summary>TEMP TM / request_cache 证据（第9.0B-P6轮）。</summary>
internal sealed class SmokeMemoryEvidence
{
    public int TranslationRowCount { get; init; }
    public int RequestCacheRowCount { get; init; }
    public int SaltChecksPassed { get; init; }
    public int SaltChecksFailed { get; init; }
    public required IReadOnlyList<string> Fingerprints { get; init; }
}


/// <summary>一个临时 JSON 记录（id + 文本）。</summary>
/// <param name="Id">记录 id（与真实文件一致；StoryData 为数字）</param>
/// <param name="Text">可翻译字段文本</param>
/// <param name="Field">字段名（默认 name；第9.0B 最终 Smoke 需要 desc / content）</param>
/// <param name="Model">说话人（model；黑名单字段，仅用于让 StoryData 记录更接近真实结构）</param>
/// <param name="OmitField">
/// 是否**不写**可翻译字段（占位行）。
/// 用途：旧树为了让行索引与当前树一致（FieldPath 不错位），必须保留目标记录的位置但不产生可翻译单元。
/// </param>
internal sealed record PhaseRecord(string Id, string Text, string Field = "name", string? Model = null, bool OmitField = false);

/// <summary>一个临时 JSON 文件（逻辑名 + 记录）。</summary>
internal sealed record PhaseFile(string LogicalName, IReadOnlyList<PhaseRecord> Records);

/// <summary>一个模式子根的三语 / 旧数据定义。</summary>
internal sealed record PhaseData(
    IReadOnlyList<PhaseFile> OldKorean,
    IReadOnlyList<PhaseFile> Korean,
    IReadOnlyList<PhaseFile> OldEnglish,
    IReadOnlyList<PhaseFile> English,
    IReadOnlyList<PhaseFile> OldChinese,
    IReadOnlyList<PhaseFile>? Japanese);

internal static partial class P6RealSmokeCommand
{
    private const string MainFile = "Items.json";
    private const string NewFile = "NewFile.json";

    /// <summary>构造 8 条矩阵（4 模式 × 2 单元）。</summary>
    private static IReadOnlyList<SmokePhase> BuildPhases(SmokeSession session)
    {
        static PhaseFile File(string name, params PhaseRecord[] records) => new(name, records);
        static PhaseRecord Rec(int id, string text) => new(id.ToString(), text);

        // ── 1/2：EN_ONLY（TranslateNew 要求“旧英文里没有该 Key” ⇒ 使用一个新文件） ──
        var enOnly = new PhaseData(
            OldKorean: new[] { File(MainFile, Rec(1, KoreanOld)) },
            Korean: new[] { File(MainFile, Rec(1, KoreanNew)) },
            OldEnglish: new[] { File(MainFile, Rec(1, EnglishOld)) },
            English: new[]
            {
                File(MainFile, Rec(1, EnglishNew)),
                File(NewFile, Rec(1, EnglishNewUnit)),
            },
            OldChinese: new[] { File(MainFile, Rec(1, "旧译文甲")) },
            Japanese: null);

        // ── 3/4：KR_EN（rec1 Modified；rec2 Unchanged 且旧中文缺失 ⇒ TranslateMissing） ──
        var krEn = new PhaseData(
            OldKorean: new[] { File(MainFile, Rec(1, KoreanOld), Rec(2, KoreanStable)) },
            Korean: new[] { File(MainFile, Rec(1, KoreanNew), Rec(2, KoreanStable)) },
            OldEnglish: new[] { File(MainFile, Rec(1, EnglishUnit3), Rec(2, EnglishUnit4)) },
            English: new[] { File(MainFile, Rec(1, EnglishUnit3), Rec(2, EnglishUnit4)) },
            OldChinese: new[] { File(MainFile, Rec(1, OldChinese3)) },
            Japanese: null);

        // ── 5/6：KR_JP（rec1 Modified 且有 JP；rec2 Modified 但 JP 缺失 ⇒ Selected 回退韩文） ──
        var krJp = new PhaseData(
            OldKorean: new[] { File(MainFile, Rec(1, KoreanOld), Rec(2, KoreanStable)) },
            Korean: new[] { File(MainFile, Rec(1, KoreanNew), Rec(2, KoreanStableChanged)) },
            OldEnglish: new[] { File(MainFile, Rec(1, "Hi there"), Rec(2, "Thanks")) },
            English: new[] { File(MainFile, Rec(1, "Hi there"), Rec(2, "Thanks")) },
            OldChinese: new[] { File(MainFile, Rec(1, OldChinese5)) },
            Japanese: new[] { File(MainFile, Rec(1, JapaneseUnit5)) });

        // ── 7/8：KR_ONLY（rec1 Modified；rec2 Unchanged 且旧中文缺失 ⇒ TranslateMissing） ──
        var krOnly = new PhaseData(
            OldKorean: new[] { File(MainFile, Rec(1, KoreanOld), Rec(2, KoreanStable)) },
            Korean: new[] { File(MainFile, Rec(1, KoreanNew), Rec(2, KoreanStable)) },
            OldEnglish: new[] { File(MainFile, Rec(1, "Hello"), Rec(2, "Thanks")) },
            English: new[] { File(MainFile, Rec(1, "Hello"), Rec(2, "Thanks")) },
            OldChinese: new[] { File(MainFile, Rec(1, OldChinese7)) },
            Japanese: null);

        return new List<SmokePhase>
        {
            BuildPhase(session, "en_only", TranslationMode.EnglishOnly, enOnly, new[]
            {
                new SmokeUnitExpectation(
                    $"{NewFile}|1|dataList[0].name", TranslationMode.EnglishOnly,
                    TranslationAction.TranslateNew, EnglishNewUnit,
                    ExpectCanonicalKorean: false, ExpectOldCanonicalKorean: false, ExpectSalt: false,
                    ExpectPlaceholder: true),
                new SmokeUnitExpectation(
                    $"{MainFile}|1|dataList[0].name", TranslationMode.EnglishOnly,
                    TranslationAction.TranslateModified, EnglishNew,
                    ExpectCanonicalKorean: false, ExpectOldCanonicalKorean: false, ExpectSalt: false),
            }),
            BuildPhase(session, "kr_en", TranslationMode.KoreanEnglish, krEn, KrPhaseUnits(
                TranslationMode.KoreanEnglish,
                expected1: EnglishUnit3, action1: TranslationAction.TranslateModified,
                expected2: EnglishUnit4, action2: TranslationAction.TranslateMissing,
                canonical1: KoreanNew, oldCanonical1: KoreanOld,
                canonical2: KoreanStable, oldCanonical2: KoreanStable,
                placeholder1: true)),
            BuildPhase(session, "kr_jp", TranslationMode.KoreanJapanese, krJp, KrPhaseUnits(
                TranslationMode.KoreanJapanese,
                expected1: JapaneseUnit5, action1: TranslationAction.TranslateModified,
                expected2: KoreanStableChanged, action2: TranslationAction.TranslateModified,
                canonical1: KoreanNew, oldCanonical1: KoreanOld,
                canonical2: KoreanStableChanged, oldCanonical2: KoreanStable,
                placeholder1: false)),
            BuildPhase(session, "kr_only", TranslationMode.KoreanOnly, krOnly, KrPhaseUnits(
                TranslationMode.KoreanOnly,
                expected1: KoreanNew, action1: TranslationAction.TranslateModified,
                expected2: KoreanStable, action2: TranslationAction.TranslateMissing,
                canonical1: KoreanNew, oldCanonical1: KoreanOld,
                canonical2: KoreanStable, oldCanonical2: KoreanStable,
                placeholder1: false)),
        };
    }

    /// <summary>KR 三模式共用同一套单元期望构造（保证语义一致、减少重复）。</summary>
    private static IReadOnlyList<SmokeUnitExpectation> KrPhaseUnits(
        TranslationMode mode,
        string expected1,
        TranslationAction action1,
        string expected2,
        TranslationAction action2,
        string canonical1,
        string oldCanonical1,
        string canonical2,
        string oldCanonical2,
        bool placeholder1)
        => new[]
        {
            new SmokeUnitExpectation(
                $"{MainFile}|1|dataList[0].name", mode, action1, expected1,
                ExpectCanonicalKorean: true, ExpectOldCanonicalKorean: true, ExpectSalt: true,
                ExpectPlaceholder: placeholder1,
                ExpectedCanonicalKoreanText: canonical1, ExpectedOldCanonicalKoreanText: oldCanonical1),
            new SmokeUnitExpectation(
                $"{MainFile}|2|dataList[1].name", mode, action2, expected2,
                ExpectCanonicalKorean: true, ExpectOldCanonicalKorean: true, ExpectSalt: true,
                ExpectedCanonicalKoreanText: canonical2, ExpectedOldCanonicalKoreanText: oldCanonical2),
        };
}

internal static partial class P6RealSmokeCommand
{
    /// <summary>写入一个模式子根：三语树 + 旧英文 / 旧中文，并建立 KR 基线后返回当前捕获结果。</summary>
    private static SmokePhase BuildPhase(
        SmokeSession session,
        string name,
        TranslationMode mode,
        PhaseData data,
        IReadOnlyList<SmokeUnitExpectation> units,
        int expectedOutputEntryCount = 0)
    {
        var root = Path.Combine(session.TempRoot, name);
        var localize = Path.Combine(root, "Localize");
        var newEnDir = Path.Combine(localize, "en");
        var oldEnDir = Path.Combine(root, "old_en");
        var oldZhDir = Path.Combine(root, "old_zh");
        var cacheDir = Path.Combine(root, "data", "cache");
        Directory.CreateDirectory(newEnDir);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "output"));

        // 旧英文 / 旧中文（不写 language 前缀 / 英文前缀）
        WriteFiles(oldEnDir, "EN_", data.OldEnglish);
        WriteFiles(oldZhDir, string.Empty, data.OldChinese);

        // ① 先写“上一版韩文”并捕获 ⇒ 建立 Previous KR 基线（不调用 API）
        WriteFiles(Path.Combine(localize, "kr"), "KR_", data.OldKorean);
        if (data.Japanese is { Count: > 0 })
        {
            WriteFiles(Path.Combine(localize, "jp"), "JP_", data.Japanese);
        }

        WriteFiles(newEnDir, "EN_", data.English);
        _ = ProductionTranslationPlanBuilder.TryCapture(
            root, newEnDir, session.ConfigDir, preParsedEnglish: null, log: session.Log);

        // ② 再写“当前韩文”并捕获 ⇒ Canonical Diff = Previous KR vs Current KR
        WriteFiles(Path.Combine(localize, "kr"), "KR_", data.Korean);
        var capture = ProductionTranslationPlanBuilder.TryCapture(
            root, newEnDir, session.ConfigDir, preParsedEnglish: null, log: session.Log);
        if (capture is null)
        {
            throw new InvalidOperationException($"[错误] 子根 {name} 的 Canonical 捕获失败（KR 目录缺失或解析不到单元）。");
        }

        return new SmokePhase
        {
            Name = name,
            Mode = mode,
            Root = root,
            NewEnglishDirectory = newEnDir,
            OldEnglishDirectory = oldEnDir,
            OldChineseDirectory = oldZhDir,
            DatabasePath = Path.Combine(cacheDir, "translation_memory.db"),
            Units = units,
            Capture = capture,
            ExpectedOutputEntryCount = expectedOutputEntryCount,
        };
    }

    /// <summary>
    /// 逻辑名 + 语言前缀 → 物理路径。
    /// **前缀只加在文件名上**（与真实游戏一致：<c>en/StoryData/EN_1D101A.json</c>），
    /// 否则会把前缀拼进目录名（<c>EN_StoryData/...</c>），导致 UnitKey 与旧中文/旧英文对不上。
    /// </summary>
    private static string ResolvePhysicalPath(string directory, string filePrefix, string logicalName)
    {
        var normalized = logicalName.Replace('\\', '/');
        var dir = Path.GetDirectoryName(normalized)?.Replace('\\', '/') ?? string.Empty;
        var fileName = Path.GetFileName(normalized);
        var relative = string.IsNullOrEmpty(dir) ? filePrefix + fileName : dir + "/" + filePrefix + fileName;
        return Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>写 JSON 文件（<c>dataList[].name</c> 形式，与生产解析规则一致）。</summary>
    private static void WriteFiles(string directory, string filePrefix, IReadOnlyList<PhaseFile> files)
    {
        Directory.CreateDirectory(directory);
        foreach (var file in files)
        {
            var rows = file.Records
                .Select(record =>
                {
                    var row = new Dictionary<string, object?>();
                    // id 保真：真实文件里 StoryData 用数字 id、Bufs 用字符串 id ⇒ 数字串写数字
                    row["id"] = record.Id.Length > 0 && record.Id.All(char.IsAsciiDigit)
                        ? long.Parse(record.Id)
                        : record.Id;
                    if (!string.IsNullOrWhiteSpace(record.Model))
                    {
                        // model 是黑名单字段（不翻译），仅用于让 StoryData 记录更接近真实结构
                        row["model"] = record.Model;
                    }

                    if (!record.OmitField)
                    {
                        row[record.Field] = record.Text;
                    }

                    return row;
                })
                .ToList();
            var json = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, object?> { ["dataList"] = rows },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var full = ResolvePhysicalPath(directory, filePrefix, file.LogicalName);
            var subDirectory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(subDirectory))
            {
                // 逻辑名可含子目录（StoryData/xxx.json）→ 必须建目录
                Directory.CreateDirectory(subDirectory);
            }

            File.WriteAllText(full, json);
        }
    }
}


internal static partial class P6RealSmokeCommand
{
    /// <summary>运行一个阶段（4 个模式子根依次执行），并做阶段级硬性检查。</summary>
    private static async Task<IReadOnlyList<SmokeStageResult>> RunStageAsync(
        SmokeSession session,
        IReadOnlyList<SmokePhase> phases,
        string stageName,
        StageExpectation expectation)
    {
        var unitsBefore = session.Budget.Units;
        var requestsBefore = session.Budget.Requests;
        session.Log($"[调试] ===== {stageName}（期望：{(expectation == StageExpectation.Network ? "真实网络" : expectation == StageExpectation.TmReplay ? "TM 命中，新增网络 0" : "Cache 命中，新增网络 0")}）=====");

        var results = new List<SmokeStageResult>();
        foreach (var phase in phases)
        {
            results.Add(await RunPhaseAsync(session, phase, stageName, expectation));
        }

        var unitDelta = session.Budget.Units - unitsBefore;
        var requestDelta = session.Budget.Requests - requestsBefore;
        session.Log($"[调试] {stageName} 汇总：单元 +{unitDelta}｜真实网络请求 +{requestDelta}｜{session.Budget.Describe()}");

        if (expectation == StageExpectation.Network)
        {
            if (unitDelta != 8)
            {
                session.Violations.Add($"{stageName}: 真实翻译单元数 {unitDelta} != 8");
            }

            if (requestDelta > SmokeBudget.MaxNetworkRequests)
            {
                session.Violations.Add($"{stageName}: 真实网络请求 {requestDelta} 超过上限 {SmokeBudget.MaxNetworkRequests}");
            }
        }
        else if (unitDelta != 0 || requestDelta != 0)
        {
            session.Violations.Add($"{stageName}: 复现阶段出现了真实网络调用（单元 +{unitDelta} / 请求 +{requestDelta}），必须为 0");
        }

        return results;
    }

    /// <summary>运行一个模式子根的单个阶段（真实链路：Diff → Plan → Agent → TM/Cache/Trace → Merge → Gate）。</summary>
    private static async Task<SmokeStageResult> RunPhaseAsync(
        SmokeSession session,
        SmokePhase phase,
        string stageName,
        StageExpectation expectation,
        bool dryRun = false)
    {
        var violations = new List<string>();
        var diffResult = new DiffWorkflowService(session.ConfigDir)
            .Analyze(phase.OldEnglishDirectory, phase.OldChineseDirectory, phase.NewEnglishDirectory, null);
        var plan = ProductionTranslationPlanBuilder.Build(
            phase.Mode, diffResult.Entries, phase.Capture, phase.NewEnglishDirectory, diffResult.OldChineseUnits,
            session.GlossarySnapshot);

        if (dryRun)
        {
            session.Log($"[调试] [{phase.Name}] dry-run：NeedTranslate={plan.NeedTranslate.Count}（期望 {phase.Units.Count}）");
            foreach (var entry in plan.Candidates)
            {
                session.Log($"[调试]   [dry-run/{phase.Name}] {entry.Key}｜Action={entry.Action}｜Kind={entry.DiffKind}"
                            + $"｜旧中文={(entry.OldTranslation is null ? "无" : (!string.IsNullOrEmpty(entry.OldTranslation) ? "有" : "空"))}"
                            + $"｜Canonical={(entry.CanonicalKoreanText is null ? "无" : "有")}");
            }
        }

        if (expectation == StageExpectation.Network && plan.NeedTranslate.Count != phase.Units.Count)
        {
            violations.Add(
                $"[{phase.Name}] 预检失败：NeedTranslate={plan.NeedTranslate.Count}，期望 {phase.Units.Count}"
                + "（矩阵与计划不一致；**未发送任何请求**）");
        }

        if (violations.Count > 0)
        {
            foreach (var violation in violations)
            {
                session.Violations.Add($"{stageName} {violation}");
            }

            return new SmokeStageResult
            {
                StageName = stageName,
                TranslationMode = TranslationModeCodes.ToCode(phase.Mode),
                AgentEntryCount = plan.NeedTranslate.Count,
                UnitDelta = 0,
                RequestDelta = 0,
                BudgetUnits = session.Budget.Units,
                BudgetRequests = session.Budget.Requests,
                TmHitCount = 0,
                TraceLineCount = 0,
                TraceNetworkCalledCount = 0,
                TraceCacheHitCount = 0,
                MergeWrittenEntryCount = 0,
                MergeIssueCount = 0,
                MergeComplete = false,
                GateMissingExpectedKeyCount = 0,
                GateUnexpectedOutputKeyCount = 0,
                GateErrorCount = 0,
                GateBlockingErrorCount = 0,
                GateStatus = "skipped",
                OutputDirectory = string.Empty,
                Units = Array.Empty<SmokeUnitSnapshot>(),
                Translations = new Dictionary<string, string>(),
                Violations = violations,
            };
        }

        // ── 真实链路：Agent（真实 DeepSeek）→ TM / RequestCache / Trace → Merge → Gate ──
        if (dryRun)
        {
            session.Log($"[调试] [{phase.Name}] dry-run 预检：NeedTranslate={plan.NeedTranslate.Count}"
                        + $"（期望 {phase.Units.Count}）｜权威结构={SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage)}｜**未发送任何请求**");
            return new SmokeStageResult
            {
                StageName = stageName,
                TranslationMode = TranslationModeCodes.ToCode(phase.Mode),
                AgentEntryCount = plan.NeedTranslate.Count,
                UnitDelta = 0,
                RequestDelta = 0,
                BudgetUnits = session.Budget.Units,
                BudgetRequests = session.Budget.Requests,
                TmHitCount = 0,
                TraceLineCount = 0,
                TraceNetworkCalledCount = 0,
                TraceCacheHitCount = 0,
                MergeWrittenEntryCount = 0,
                MergeIssueCount = 0,
                MergeComplete = true,
                GateMissingExpectedKeyCount = 0,
                GateUnexpectedOutputKeyCount = 0,
                GateErrorCount = 0,
                GateBlockingErrorCount = 0,
                GateStatus = "dry-run",
                OutputDirectory = string.Empty,
                Units = Array.Empty<SmokeUnitSnapshot>(),
                Translations = new Dictionary<string, string>(),
                Violations = violations,
                DryRun = true,
            };
        }

        var unitsBefore = session.Budget.Units;
        var requestsBefore = session.Budget.Requests;

        var runContext = TranslationRunContext.Create();
        session.Log($"[调试] [{phase.Name}] RunId={runContext.RunId}｜模式={TranslationModeCodes.ToCode(phase.Mode)}"
                    + $"｜权威结构={SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage)}｜待翻译 {plan.NeedTranslate.Count} 条");

        var traceWriter = new TranslationTraceWriter(phase.Root, runContext, session.Log);
        using var memory = new SqliteTranslationMemory(
            new TranslationMemoryOptions { DatabasePath = phase.DatabasePath }, session.Log);
        var cacheServices = TranslationCacheServices.Create(memory, runContext, traceWriter, session.Log);

        // 第9.0B 最终轮：邻句上下文索引（与生产同源：来源语言按模式选择）
        var neighborUnits = ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(
            phase.Mode, phase.Capture, diffResult.NewUnits);
        var contextIndex = TranslationContextIndex.Build(neighborUnits, session.Log);
        session.Log($"[调试] [{phase.Name}] 邻句上下文索引：作用域 {contextIndex.ScopeCount}｜对话行 {contextIndex.NodeCount}"
                    + $"｜来源语言={SourceLanguageHelper.ToCode(TranslationModePolicy.GetNeighborSourceLanguage(phase.Mode))}"
                    + $"｜术语注入 {plan.MatchedTermsInjectedCount} 条");
        var contextBuilder = new TranslationContextBuilder(contextIndex);

        using var innerClient = new DeepSeekClient(session.Options, PromptLoader.Load(session.ConfigDir));
        var cappedClient = new SmokeCappedBatchClient(innerClient, session.Budget, session.Log);
        using var provider = new DeepSeekTranslationProvider(
            session.Options,
            session.ConfigDir,
            session.Batch,
            cacheServices: cacheServices,
            client: cappedClient,
            log: session.Log,
            thinkingPolicy: null,
            glossarySnapshot: session.GlossarySnapshot,
            translationMode: phase.Mode);
        var validation = ValidationPipeline.CreateDefault(session.GlossarySnapshot);
        var coordinator = new Coordinator(
            provider,
            memory,
            maxConcurrentAgents: 1,
            maxConcurrentApiRequests: 1,
            log: session.Log,
            validation: validation,
            cacheServices: cacheServices,
            contextBuilder: contextBuilder);

        var coordinatorResult = await coordinator.ExecuteAsync(plan.NeedTranslate);
        if (coordinatorResult.FailedCount != 0)
        {
            violations.Add($"[{phase.Name}] Agent 失败 {coordinatorResult.FailedCount} 个 Stage");
        }

        var traceEntries = ReadTrace(traceWriter.FilePath).ToList();
        var translations = Coordinator.CollectTranslations(plan.OutputEntries);
        var outputRoot = Path.Combine(phase.Root, "output", stageName);
        var merge = new MergeOutputService().MergeAllWithReport(
            plan.AuthoritativeDirectory,
            translations,
            outputRoot,
            plan.ExpectedOutputKeys,
            plan.AuthoritativeLanguage);
        var gate = ReleaseGateService.Evaluate(
            plan.OutputEntries,
            validation,
            null,
            new ReleaseGateKeySet
            {
                ExpectedKeys = plan.ExpectedOutputKeys,
                OutputKeys = merge.WrittenKeys,
                AuthoritativeSourceCode = SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage),
            });

        var snapshots = new List<SmokeUnitSnapshot>();
        foreach (var unit in phase.Units)
        {
            snapshots.Add(BuildSnapshot(session, phase, plan, cappedClient, traceEntries, expectation, unit, violations));
        }

        // ── 阶段级结构断言（Merge / Gate） ──
        if (!merge.IsComplete)
        {
            violations.Add($"[{phase.Name}] Merge 未完成：{merge.Issues.Count} 个问题（{DescribeIssues(merge)}）");
        }

        if (merge.WrittenEntryCount != ExpectedOutputEntryCount(phase))
        {
            violations.Add($"[{phase.Name}] Merge 写入条目 {merge.WrittenEntryCount} != 预期 {ExpectedOutputEntryCount(phase)}");
        }

        if (gate.MissingExpectedKeyCount != 0)
        {
            violations.Add($"[{phase.Name}] ReleaseGate MissingExpectedKeyCount={gate.MissingExpectedKeyCount}");
        }

        if (gate.UnexpectedOutputKeyCount != 0)
        {
            violations.Add($"[{phase.Name}] ReleaseGate UnexpectedOutputKeyCount={gate.UnexpectedOutputKeyCount}");
        }

        if (gate.ErrorCount != 0 || gate.BlockingErrorCount != 0)
        {
            violations.Add(
                $"[{phase.Name}] ReleaseGate 结构错误：Error {gate.ErrorCount}（阻断 {gate.BlockingErrorCount}），Status={gate.Status}");
        }

        // ── 阶段级期望断言 ──
        switch (expectation)
        {
            case StageExpectation.Network:
                if (traceEntries.Count(line => line.NetworkCalled) == 0)
                {
                    violations.Add($"[{phase.Name}] Stage1 未观察到 networkCalled=true 的 Trace");
                }

                break;
            case StageExpectation.TmReplay:
                if (snapshots.Any(snapshot => snapshot.TmMatchType != "ExactUnit"))
                {
                    violations.Add($"[{phase.Name}] TM Replay 未全部命中 ExactUnit，实际：{string.Join(",", snapshots.Select(s => s.TmMatchType))}");
                }

                break;
            case StageExpectation.CacheReplay:
                if (traceEntries.Count(line => line.CacheHit) == 0)
                {
                    violations.Add($"[{phase.Name}] RequestCache Replay 未观察到 cacheHit=true 的 Trace");
                }

                break;
        }

        foreach (var violation in violations)
        {
            session.Violations.Add($"{stageName} {violation}");
        }

        session.Log($"[调试] [{phase.Name}] 完成：Trace {traceEntries.Count} 行（网络 {traceEntries.Count(l => l.NetworkCalled)}／缓存命中 {traceEntries.Count(l => l.CacheHit)}）"
                    + $"｜TM 命中 {coordinatorResult.TotalTmHits} 条｜Merge 写入 {merge.WrittenEntryCount} 条｜Gate {gate.Status}"
                    + $"｜Merge 问题 {merge.Issues.Count}");

        return new SmokeStageResult
        {
            StageName = stageName,
            TranslationMode = TranslationModeCodes.ToCode(phase.Mode),
            AgentEntryCount = plan.NeedTranslate.Count,
            ProviderCallCount = traceEntries.Count,
            UnitDelta = session.Budget.Units - unitsBefore,
            RequestDelta = session.Budget.Requests - requestsBefore,
            BudgetUnits = session.Budget.Units,
            BudgetRequests = session.Budget.Requests,
            TmHitCount = coordinatorResult.TotalTmHits,
            TraceLineCount = traceEntries.Count,
            TraceNetworkCalledCount = traceEntries.Count(line => line.NetworkCalled),
            TraceCacheHitCount = traceEntries.Count(line => line.CacheHit),
            MergeWrittenEntryCount = merge.WrittenEntryCount,
            MergeIssueCount = merge.Issues.Count,
            MergeComplete = merge.IsComplete,
            GateMissingExpectedKeyCount = gate.MissingExpectedKeyCount,
            GateUnexpectedOutputKeyCount = gate.UnexpectedOutputKeyCount,
            GateErrorCount = gate.ErrorCount,
            GateBlockingErrorCount = gate.BlockingErrorCount,
            GateStatus = gate.Status.ToString(),
            OutputDirectory = outputRoot,
            Units = snapshots,
            Translations = translations,
            Violations = violations,
        };
    }

    /// <summary>该模式子根期望写入 output 的条目数（未显式设置时 = 待翻译单元数）。</summary>
    private static int ExpectedOutputEntryCount(SmokePhase phase)
        => phase.ExpectedOutputEntryCount > 0 ? phase.ExpectedOutputEntryCount : phase.Units.Count;

    /// <summary>单条单元的结构 + 真实请求语义快照（SafeRequestSemanticSnapshot）。</summary>
    private static SmokeUnitSnapshot BuildSnapshot(
        SmokeSession session,
        SmokePhase phase,
        ProductionTranslationPlan plan,
        SmokeCappedBatchClient client,
        IReadOnlyList<TranslationTraceEntry> traceEntries,
        StageExpectation expectation,
        SmokeUnitExpectation unit,
        List<string> violations)
    {
        var matches = plan.OutputEntries.Where(entry => entry.Key.ToString() == unit.UnitKey).ToList();
        if (matches.Count != 1)
        {
            violations.Add($"[{phase.Name}] 单元 {unit.UnitKey} 在计划里出现 {matches.Count} 次（期望 1）");
            return new SmokeUnitSnapshot
            {
                UnitKey = unit.UnitKey,
                StageId = phase.Name,
                Mode = TranslationModeCodes.ToCode(unit.Mode),
                Action = "missing",
            };
        }

        var entry = matches[0];
        var sent = client.SentItems.FirstOrDefault(item => item.Id == unit.UnitKey);
        var trace = traceEntries.FirstOrDefault(line => line.UnitKeys.Contains(unit.UnitKey));
        var translation = entry.Translation ?? string.Empty;

        void Check(bool condition, string message)
        {
            if (!condition)
            {
                violations.Add($"[{phase.Name}] {message}");
            }
        }

        Check(entry.Action == unit.ExpectedAction, $"单元 {unit.UnitKey} Action={entry.Action}（期望 {unit.ExpectedAction}）");
        Check(entry.NewSourceText == unit.ExpectedSelectedText,
            $"单元 {unit.UnitKey} SelectedSource={entry.NewSourceText}（期望 {unit.ExpectedSelectedText}）");
        Check((entry.CanonicalKoreanText is not null) == unit.ExpectCanonicalKorean,
            $"单元 {unit.UnitKey} CanonicalKorean 存在性={entry.CanonicalKoreanText is not null}（期望 {unit.ExpectCanonicalKorean}）");
        Check((entry.OldCanonicalKoreanText is not null) == unit.ExpectOldCanonicalKorean,
            $"单元 {unit.UnitKey} OldCanonicalKorean 存在性={entry.OldCanonicalKoreanText is not null}（期望 {unit.ExpectOldCanonicalKorean}）");
        Check((entry.SourceHashSalt is not null) == unit.ExpectSalt,
            $"单元 {unit.UnitKey} SourceHashSalt 存在性={entry.SourceHashSalt is not null}（期望 {unit.ExpectSalt}）");

        if (unit.ExpectedCanonicalKoreanText is not null)
        {
            Check(entry.CanonicalKoreanText == unit.ExpectedCanonicalKoreanText,
                $"单元 {unit.UnitKey} CanonicalKorean={entry.CanonicalKoreanText}（期望 {unit.ExpectedCanonicalKoreanText}）");
        }

        if (unit.ExpectedOldCanonicalKoreanText is not null)
        {
            Check(entry.OldCanonicalKoreanText == unit.ExpectedOldCanonicalKoreanText,
                $"单元 {unit.UnitKey} OldCanonicalKorean={entry.OldCanonicalKoreanText}（期望 {unit.ExpectedOldCanonicalKoreanText}）");
        }

        Check(!string.IsNullOrWhiteSpace(translation), $"单元 {unit.UnitKey} 真实返回为空");
        if (unit.ExpectPlaceholder)
        {
            Check(translation.Contains(SmokeUnitExpectation.Placeholder, StringComparison.Ordinal),
                $"单元 {unit.UnitKey} 占位符 {SmokeUnitExpectation.Placeholder} 丢失：{translation}");
        }

        if (expectation == StageExpectation.Network)
        {
            // 真实请求语义：EN_ONLY 绝不发送韩文区块；KR 模式必须发送（Modified 还需发送旧韩文）
            Check(sent is not null, $"单元 {unit.UnitKey} 未观察到真实请求项（Provider 未被调用？）");
            if (sent is not null)
            {
                Check(string.IsNullOrWhiteSpace(sent.CanonicalKorean) == !unit.ExpectCanonicalKorean,
                    $"单元 {unit.UnitKey} 真实请求 CanonicalKorean 存在性={!string.IsNullOrWhiteSpace(sent.CanonicalKorean)}（期望 {unit.ExpectCanonicalKorean}）");
                Check(string.IsNullOrWhiteSpace(sent.OldCanonicalKorean) == !unit.ExpectOldCanonicalKorean,
                    $"单元 {unit.UnitKey} 真实请求 OldCanonicalKorean 存在性={!string.IsNullOrWhiteSpace(sent.OldCanonicalKorean)}（期望 {unit.ExpectOldCanonicalKorean}）");
                Check(!string.IsNullOrWhiteSpace(sent.OldTranslation) == (entry.OldTranslation is not null),
                    $"单元 {unit.UnitKey} 真实请求 PreviousTarget 存在性={!string.IsNullOrWhiteSpace(sent.OldTranslation)}（期望 {entry.OldTranslation is not null}）");
            }
        }

        foreach (var issue in entry.ValidationIssues.Where(i => i.Severity == ValidationSeverity.Error))
        {
            violations.Add($"[{phase.Name}] 单元 {unit.UnitKey} Validator Error：{issue.Code} {issue.Message}");
        }

        return new SmokeUnitSnapshot
        {
            UnitKey = unit.UnitKey,
            StageId = phase.Name,
            Mode = TranslationModeCodes.ToCode(unit.Mode),
            Action = entry.Action.ToString(),
            SelectedSourceText = entry.NewSourceText,
            CanonicalKoreanText = entry.CanonicalKoreanText,
            OldCanonicalKoreanText = entry.OldCanonicalKoreanText,
            PreviousTargetText = entry.OldTranslation,
            SourceHashSalt = entry.SourceHashSalt,
            Fingerprint = trace?.Fingerprint,
            HasGlossarySnapshot = session.GlossarySnapshot.Count > 0,
            CanonicalKoreanSent = !string.IsNullOrWhiteSpace(sent?.CanonicalKorean),
            OldCanonicalKoreanSent = !string.IsNullOrWhiteSpace(sent?.OldCanonicalKorean),
            PlaceholderProtectedInRequest = sent?.Source?.Contains("__LT_PH_", StringComparison.Ordinal) == true,
            CacheHit = trace?.CacheHit ?? false,
            NetworkCalled = trace?.NetworkCalled ?? false,
            Translation = entry.Translation,
            TranslationNonEmpty = !string.IsNullOrWhiteSpace(entry.Translation),
            TmMatchType = entry.TmMatchType.ToString(),
            ValidationErrorCount = entry.ValidationIssues.Count(i => i.Severity == ValidationSeverity.Error),
            ValidationWarningCount = entry.ValidationIssues.Count(i => i.Severity == ValidationSeverity.Warning),
            NeedsReview = entry.NeedsReview,
            Provenance = entry.Provenance?.ToString(),
            PlaceholderPreserved = !unit.ExpectPlaceholder
                                  || entry.Translation?.Contains(SmokeUnitExpectation.Placeholder, StringComparison.Ordinal) == true,
            // 第9.0B 最终真实四模式 Smoke：Trace 四模式字段 + Token + 术语 + 邻接上下文（全部来自真实请求）
            TraceTranslationMode = trace?.TranslationMode,
            EffectiveSourceLanguage = trace?.EffectiveSourceLanguage,
            UsedKoreanFallback = trace?.UsedKoreanFallback,
            CanonicalKoreanPresent = trace?.CanonicalKoreanPresent,
            CanonicalChanged = trace?.CanonicalChanged,
            EnglishChanged = trace?.EnglishChanged,
            JapaneseChanged = trace?.JapaneseChanged,
            ThinkingEnabled = trace?.ThinkingEnabled,
            ThinkingPolicyReason = trace?.ThinkingPolicyReason,
            InputTokens = trace?.InputTokens,
            OutputTokens = trace?.OutputTokens,
            ReasoningTokens = trace?.ReasoningTokens,
            TotalTokens = trace?.TotalTokens,
            MatchedTerms = entry.MatchedTerms?.Select(term => term.Source).ToList() ?? new List<string>(),
            NeighborPrevious = sent?.Context?.Previous?.SourceText,
            NeighborNext = sent?.Context?.Next?.SourceText,
            ValidationIssueCodes = entry.ValidationIssues.Select(issue => issue.Code).ToList(),
            ReviewReason = entry.ReviewReason,
        };
    }

    /// <summary>读取 Trace（JSONL；只读，不含凭据）。</summary>
    private static IReadOnlyList<TranslationTraceEntry> ReadTrace(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return Array.Empty<TranslationTraceEntry>();
        }

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        };
        return File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => System.Text.Json.JsonSerializer.Deserialize<TranslationTraceEntry>(line, jsonOptions)!)
            .ToList();
    }

    /// <summary>Merge 问题摘要（只取前 3 条，避免日志过长）。</summary>
    private static string DescribeIssues(OutputMergeResult merge)
        => string.Join("；", merge.Issues.Take(3).Select(issue => $"{issue.Kind}:{issue.TranslationKey ?? issue.RelativeFilePath}"));
}





