using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Parsing;

namespace LimbusTranslator.Cli;

/// <summary>
/// 第9.0B 最终真实四模式 Smoke：**从真实游戏数据**抽取 2 条单元（1 条普通 UI + 1 条 StoryData），
/// 分别跑 4 种翻译模式 = 8 条真实翻译单元 / ≤8 次真实网络请求。
///
/// 复用 P6 Harness 的同一套机制（<see cref="SmokeBudget"/> 硬上限、<see cref="SmokeCappedBatchClient"/>
/// 计数客户端、<c>BuildPhase</c> TEMP 三语树、<c>RunPhaseAsync</c> 真实生产链），不另写第二套 Smoke。
/// 与 P6 的差异只有三点：
///   1. 文本来自真实游戏数据（只读），不是人工构造；
///   2. 每个模式子根 2 条单元（1 普通 UI + 1 StoryData，带真实邻接上下文）；
///   3. 额外产出四模式对比产物（<c>four_mode_comparison.md</c> / <c>smoke_results.json</c>）。
/// </summary>
internal sealed record FourModeSample(
    string Label,
    string LogicalFile,
    string RecordId,
    string Field,
    string Korean,
    string English,
    string Japanese,
    IReadOnlyList<FourModeNeighbor> Neighbors)
{
    public bool HasJapanese => !string.IsNullOrWhiteSpace(Japanese);
}

/// <summary>真实邻接记录（用于 StoryData 的 Previous / Next 上下文）。</summary>
internal sealed record FourModeNeighbor(string RecordId, string Korean, string English, string Japanese, string? Model);

/// <summary>真实样本只读抽取（游戏目录绝不写入）。</summary>
internal static class FourModeSampleReader
{
    /// <summary>读取某语言某逻辑文件某记录的可翻译字段（不存在 → null）。</summary>
    public static string? ReadField(
        string localizeRoot,
        SourceLanguage language,
        string logicalFile,
        string recordId,
        string field)
        => ReadRecord(localizeRoot, language, logicalFile, recordId)?.GetProperty(field) is { ValueKind: JsonValueKind.String } value
            ? value.GetString()
            : null;

    /// <summary>读取某语言某逻辑文件某记录（不存在 → null）。</summary>
    public static JsonElement? ReadRecord(
        string localizeRoot,
        SourceLanguage language,
        string logicalFile,
        string recordId)
    {
        var physical = LanguageFileMapper.ToPhysicalRelativePath(language, logicalFile);
        var full = Path.Combine(
            localizeRoot,
            SourceLanguageHelper.GetLocalizeDirectoryName(language),
            physical.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(full))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(full));
        if (!doc.RootElement.TryGetProperty("dataList", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var record in list.EnumerateArray())
        {
            if (ReadId(record) == recordId)
            {
                return record.Clone();   // JsonDocument 释放后仍可用
            }
        }

        return null;
    }

    private static string ReadId(JsonElement record)
        => record.TryGetProperty("id", out var id)
            ? id.ValueKind switch
            {
                JsonValueKind.Number => id.GetRawText(),
                JsonValueKind.String => id.GetString() ?? string.Empty,
                _ => string.Empty,
            }
            : string.Empty;
}

internal static partial class P6RealSmokeCommand
{
    /// <summary>
    /// 本轮固定样本（真实单元；KR / EN / JP 均存在；长度适中、含实际语义）。
    ///   ① 普通 UI：<c>Bufs_Mirror7.json</c> 的 <c>Inspire</c> → <c>desc</c>（含 +10% 数字，可验证数字保留）
    ///   ② StoryData：<c>StoryData/1D101A.json</c> 的 <c>id=6</c> → <c>content</c>，前后各 1 条真实邻句
    /// </summary>
    private static readonly (string LogicalFile, string RecordId, string Field, string[] NeighborIds, string Label)[] FourModeSpecs =
    {
        ("Bufs_Mirror7.json", "Inspire", "desc", Array.Empty<string>(), "普通UI(非StoryData)"),
        ("StoryData/1D101A.json", "6", "content", new[] { "5", "7" }, "StoryData"),
    };

    /// <summary>第9.0B 最终真实四模式 Smoke 入口（仅由 CLI 显式 flag 调用）。</summary>
    public static async Task<int> RunFourModesAsync(string projectRoot, string? runId, string localizeRoot, bool dryRun = false)
    {
        var configDir = Path.Combine(projectRoot, "config");
        Console.WriteLine("[调试] ===== 第9.0B 最终真实四模式 Smoke（2 条真实单元 × 4 模式 = 8 单元）=====");
        Console.WriteLine($"[调试] 硬上限：单元 <= {SmokeBudget.MaxTranslationUnits}，真实网络请求 <= {SmokeBudget.MaxNetworkRequests}");

        // ① 凭据检测（fail-closed；只输出布尔，永不打印 Key）
        var settings = AppSettingsLoader.LoadProviderSettings(configDir);
        var credentialDetected = settings.Success
                                 && settings.Mode == TranslationProviderMode.DeepSeek
                                 && !string.IsNullOrWhiteSpace(settings.Options.ApiKey);
        Console.WriteLine($"[调试] API credential detected = {credentialDetected}（provider={settings.Mode}）");
        if (!credentialDetected)
        {
            Console.WriteLine("[错误] BLOCKED_NO_API_CREDENTIAL：未检测到有效 DeepSeek 凭据（或 provider 不是 deepseek）。已停止，未调用任何 API。");
            foreach (var error in settings.Errors)
            {
                Console.WriteLine($"[错误]   {error}");
            }

            return 3;
        }

        var options = new DeepSeekOptions
        {
            ApiUrl = settings.Options.ApiUrl,
            ApiKey = settings.Options.ApiKey,
            Model = settings.Options.Model,
            Thinking = settings.Options.Thinking,
            ThinkingMode = settings.Options.ThinkingMode,
            ReasoningEffort = settings.Options.ReasoningEffort,
            Temperature = settings.Options.Temperature,
            MaxTokens = settings.Options.MaxTokens,
            TimeoutSeconds = settings.Options.TimeoutSeconds,
            MaxConcurrentRequests = 1,
            MaxRetry = 0,   // 1 次逻辑调用 = 1 次 HTTP 尝试，硬上限才可精确计数
        };

        Console.WriteLine($"[调试] Provider={DeepSeekTranslationProvider.ProviderName}｜Model={options.Model}｜Host={new Uri(options.ApiUrl).Host}");
        Console.WriteLine($"[调试] Thinking：配置 mode={options.ThinkingMode?.ToString() ?? "(null)"}／thinking={options.Thinking}；MaxRetry={options.MaxRetry}");

        var glossarySnapshot = ActiveGlossarySnapshot.LoadWithParatranz(
            configDir, AppSettingsLoader.LoadParatranz(configDir), out var glossaryMerge);
        Console.WriteLine($"[调试] 术语快照 {glossarySnapshot.Count} 条（hash={glossarySnapshot.SnapshotHash}）｜{glossaryMerge.Describe()}");

        var budget = new SmokeBudget();
        var effectiveRunId = string.IsNullOrWhiteSpace(runId)
            ? DateTime.Now.ToString("yyyyMMdd_HHmmss")
            : runId!;
        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"limbus_real_smoke_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}_four_modes");
        Directory.CreateDirectory(tempRoot);
        Console.WriteLine($"[调试] TEMP Root: {tempRoot}");

        var session = new SmokeSession(configDir, tempRoot, options, settings.Batch, glossarySnapshot, budget);
        var safetyBefore = ProductionSafetyProbe.Capture(projectRoot, localizeRoot);

        try
        {
            // ② 只读抽取 2 条真实单元
            var samples = LoadFourModeSamples(localizeRoot, session);
            foreach (var sample in samples)
            {
                session.Log($"[调试] 样本 {sample.Label}：{sample.LogicalFile}|{sample.RecordId}|{sample.Field}"
                            + $"｜KR={sample.Korean}｜EN={sample.English}｜JP={sample.Japanese}"
                            + $"｜邻句 {sample.Neighbors.Count} 条");
            }

            // ③ 构造 4 个模式子根（每根 2 条真实单元；邻接记录已继承、不产生额外请求）
            var phases = BuildFourModePhases(session, samples);
            session.Log($"[调试] 已准备 {phases.Count} 个模式子根，共 {phases.Sum(phase => phase.Units.Count)} 条待翻译单元");

            // ④ Stage 1：真实网络（8 单元 / ≤8 请求；dry-run 时只做预检）
            var stage = await RunFourModeStageAsync(session, phases, dryRun);

            // ⑤ 生产安全复核 + 汇总产物
            return FinishFourModes(session, samples, phases, stage, effectiveRunId, projectRoot, localizeRoot, safetyBefore, dryRun);
        }
        catch (SmokeBudgetExceededException ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine("[错误] 真实 API Smoke 已因硬上限保护终止。");
            return 4;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[错误] 真实 API 四模式 Smoke 失败: {ex.Message}");
            return 2;
        }
    }

    /// <summary>只读抽取 2 条真实样本（KR/EN/JP 必须同时存在；缺失即 fail-closed，不猜测）。</summary>
    private static IReadOnlyList<FourModeSample> LoadFourModeSamples(string localizeRoot, SmokeSession session)
    {
        if (!Directory.Exists(localizeRoot))
        {
            throw new InvalidOperationException($"[错误] 未找到 Localize 根目录：{localizeRoot}");
        }

        session.Log($"[调试] 只读样本源：{localizeRoot}（本轮绝不写入）");
        var samples = new List<FourModeSample>();
        foreach (var (logicalFile, recordId, field, neighborIds, label) in FourModeSpecs)
        {
            var korean = FourModeSampleReader.ReadField(localizeRoot, SourceLanguage.Korean, logicalFile, recordId, field);
            var english = FourModeSampleReader.ReadField(localizeRoot, SourceLanguage.English, logicalFile, recordId, field);
            var japanese = FourModeSampleReader.ReadField(localizeRoot, SourceLanguage.Japanese, logicalFile, recordId, field);
            if (string.IsNullOrWhiteSpace(korean) || string.IsNullOrWhiteSpace(english) || string.IsNullOrWhiteSpace(japanese))
            {
                throw new InvalidOperationException(
                    $"[错误] 样本 {label} 缺语言文本：{logicalFile}|{recordId}|{field}"
                    + $"（KR={(korean is null ? "null" : "有")} / EN={(english is null ? "null" : "有")} / JP={(japanese is null ? "null" : "有")}）");
            }

            var neighbors = new List<FourModeNeighbor>();
            foreach (var neighborId in neighborIds)
            {
                var neighborKorean = FourModeSampleReader.ReadField(localizeRoot, SourceLanguage.Korean, logicalFile, neighborId, field);
                var neighborEnglish = FourModeSampleReader.ReadField(localizeRoot, SourceLanguage.English, logicalFile, neighborId, field);
                var neighborJapanese = FourModeSampleReader.ReadField(localizeRoot, SourceLanguage.Japanese, logicalFile, neighborId, field);
                if (string.IsNullOrWhiteSpace(neighborKorean) || string.IsNullOrWhiteSpace(neighborEnglish))
                {
                    throw new InvalidOperationException(
                        $"[错误] 样本 {label} 的邻接记录 {neighborId} 缺 KR/EN 文本，无法构造继承邻句。");
                }

                var record = FourModeSampleReader.ReadRecord(localizeRoot, SourceLanguage.Korean, logicalFile, neighborId);
                var model = record is { } element && element.TryGetProperty("model", out var modelElement)
                            && modelElement.ValueKind == JsonValueKind.String
                    ? modelElement.GetString()
                    : null;
                neighbors.Add(new FourModeNeighbor(neighborId, neighborKorean!, neighborEnglish!, neighborJapanese ?? string.Empty, model));
            }

            samples.Add(new FourModeSample(label, logicalFile, recordId, field, korean!, english!, japanese!, neighbors));
        }

        return samples;
    }

    /// <summary>构造 4 个模式子根（每根 2 条真实单元；邻接记录按 Inherit 保留，不产生额外请求）。</summary>
    private static IReadOnlyList<SmokePhase> BuildFourModePhases(SmokeSession session, IReadOnlyList<FourModeSample> samples)
    {
        var modes = new[]
        {
            (TranslationMode.EnglishOnly, "four_en_only"),
            (TranslationMode.KoreanEnglish, "four_kr_en"),
            (TranslationMode.KoreanJapanese, "four_kr_jp"),
            (TranslationMode.KoreanOnly, "four_kr_only"),
        };

        var plans = samples.Select(BuildFourModeFilePlan).ToList();
        var phases = new List<SmokePhase>();
        foreach (var (mode, name) in modes)
        {
            var data = BuildFourModeData(plans);
            var units = plans.Select(plan => BuildFourModeExpectation(plan, mode)).ToList();
            phases.Add(BuildPhase(
                session, name, mode, data, units,
                expectedOutputEntryCount: plans.Sum(plan => plan.OrderedRecords.Count)));
        }

        return phases;
    }

    /// <summary>一个样本文件在 TEMP 三语树里的记录布局（目标记录 + 真实邻接记录，保持真实顺序）。</summary>
    private sealed record FourModeFilePlan(
        FourModeSample Sample,
        IReadOnlyList<PhaseRecord> OrderedRecords,
        int TargetIndex,
        IReadOnlyList<PhaseRecord> NeighborRecords);

    private static FourModeFilePlan BuildFourModeFilePlan(FourModeSample sample)
    {
        var entries = new List<(long SortKey, PhaseRecord Record, bool IsTarget)>
        {
            (SortKeyOf(sample.RecordId), new PhaseRecord(sample.RecordId, sample.Korean, sample.Field), true),
        };
        foreach (var neighbor in sample.Neighbors)
        {
            entries.Add((
                SortKeyOf(neighbor.RecordId),
                new PhaseRecord(neighbor.RecordId, neighbor.Korean, sample.Field, neighbor.Model),
                false));
        }

        var ordered = entries.OrderBy(entry => entry.SortKey).ToList();
        var targetIndex = ordered.FindIndex(entry => entry.IsTarget);
        return new FourModeFilePlan(
            sample,
            ordered.Select(entry => entry.Record).ToList(),
            targetIndex,
            ordered.Where(entry => !entry.IsTarget).Select(entry => entry.Record).ToList());
    }

    /// <summary>排序键：数字 id 用数值；非数字 id 用稳定大值（保持原顺序）。</summary>
    private static long SortKeyOf(string recordId)
        => recordId.Length > 0 && recordId.All(char.IsAsciiDigit) ? long.Parse(recordId) : long.MaxValue;

    /// <summary>构造一个模式子根的三语 / 旧数据（全部真实文本）。</summary>
    private static PhaseData BuildFourModeData(IReadOnlyList<FourModeFilePlan> plans)
    {
        // 当前树：目标 + 真实邻接记录（三语齐全）
        var korean = plans.Select(plan => new PhaseFile(plan.Sample.LogicalFile, plan.OrderedRecords)).ToList();
        var english = plans.Select(plan => BuildCurrentTreeFile(plan, SourceLanguage.English)).ToList();
        var japanese = plans.Select(plan => BuildCurrentTreeFile(plan, SourceLanguage.Japanese)).ToList();

        // 旧树：**逐行与当前树对齐**（目标行保留位置但不写可翻译字段 ⇒ 不产生单元）
        //   ① 目标 ⇒ 判定 Added / New ⇒ 一定会真实翻译；
        //   ② 邻句 ⇒ 文本未变 + 旧中文存在 ⇒ Inherit（不产生额外请求）；
        //   ③ 索引对齐 ⇒ 邻句 FieldPath 与当前树一致（否则旧中文按 Key 匹配不到）。
        var oldKorean = plans
            .Select(plan => BuildOldTreeFile(plan, recordId => TextOf(plan.Sample, recordId, SourceLanguage.Korean)))
            .ToList();
        var oldEnglish = plans
            .Select(plan => BuildOldTreeFile(plan, recordId => TextOf(plan.Sample, recordId, SourceLanguage.English)))
            .ToList();
        var oldChinese = plans.Select(plan => BuildOldTreeFile(plan, recordId => $"（{recordId} 邻句旧译文）")).ToList();

        return new PhaseData(oldKorean, korean, oldEnglish, english, oldChinese, japanese);
    }

    /// <summary>当前树文件：全部记录（真实目标 + 真实邻接）按序写入。</summary>
    private static PhaseFile BuildCurrentTreeFile(FourModeFilePlan plan, SourceLanguage language)
        => new(
            plan.Sample.LogicalFile,
            plan.OrderedRecords
                .Select(record => new PhaseRecord(
                    record.Id,
                    TextOf(plan.Sample, record.Id, language),
                    record.Field,
                    record.Model))
                .ToList());

    /// <summary>旧树文件：目标行占位（不写字段），其余行按文本选择器写入。</summary>
    private static PhaseFile BuildOldTreeFile(FourModeFilePlan plan, Func<string, string> neighborTextSelector)
        => new(
            plan.Sample.LogicalFile,
            plan.OrderedRecords
                .Select(record => record.Id == plan.Sample.RecordId
                    ? new PhaseRecord(record.Id, string.Empty, record.Field, record.Model, OmitField: true)
                    : new PhaseRecord(record.Id, neighborTextSelector(record.Id), record.Field, record.Model))
                .ToList());

    /// <summary>样本记录 id 对应语言的真实文本（目标或邻接）。</summary>
    private static string TextOf(FourModeSample sample, string recordId, SourceLanguage language)
    {
        if (recordId == sample.RecordId)
        {
            return language switch
            {
                SourceLanguage.English => sample.English,
                SourceLanguage.Japanese => sample.Japanese,
                _ => sample.Korean,
            };
        }

        var neighbor = sample.Neighbors.First(item => item.RecordId == recordId);
        return language switch
        {
            SourceLanguage.English => neighbor.English,
            SourceLanguage.Japanese => neighbor.Japanese,
            _ => neighbor.Korean,
        };
    }

    /// <summary>构造单条真实单元的期望（Action / Selected Source / Canonical / Salt）。</summary>
    private static SmokeUnitExpectation BuildFourModeExpectation(FourModeFilePlan plan, TranslationMode mode)
    {
        var sample = plan.Sample;
        var unitKey = $"{sample.LogicalFile}|{sample.RecordId}|dataList[{plan.TargetIndex}].{sample.Field}";
        var (selected, expectCanonical, expectSalt) = mode switch
        {
            TranslationMode.EnglishOnly => (sample.English, false, false),
            TranslationMode.KoreanEnglish => (sample.English, true, true),
            TranslationMode.KoreanJapanese => (sample.Japanese, true, true),
            _ => (sample.Korean, true, true),
        };

        return new SmokeUnitExpectation(
            unitKey,
            mode,
            TranslationAction.TranslateNew,   // 目标记录只存在于当前树 ⇒ New（真实“新增条目”场景）
            selected,
            ExpectCanonicalKorean: expectCanonical,
            ExpectOldCanonicalKorean: false,
            ExpectSalt: expectSalt,
            ExpectedCanonicalKoreanText: expectCanonical ? sample.Korean : null);
    }

    /// <summary>运行真实网络阶段（8 单元 / ≤8 请求）并做硬性检查。</summary>
    private static async Task<IReadOnlyList<SmokeStageResult>> RunFourModeStageAsync(
        SmokeSession session,
        IReadOnlyList<SmokePhase> phases,
        bool dryRun)
    {
        var unitsBefore = session.Budget.Units;
        var requestsBefore = session.Budget.Requests;
        session.Log($"[调试] ===== stage1_real_network（4 模式 × 2 条真实单元{(dryRun ? "；dry-run 仅预检" : string.Empty)}）=====");

        var results = new List<SmokeStageResult>();
        foreach (var phase in phases)
        {
            results.Add(await RunPhaseAsync(session, phase, "stage1_real_network", StageExpectation.Network, dryRun));
        }

        if (dryRun)
        {
            session.Log("[调试] dry-run 完成：未发送任何真实请求（仅校验矩阵与计划是否一致）。");
            return results;
        }

        var unitDelta = session.Budget.Units - unitsBefore;
        var requestDelta = session.Budget.Requests - requestsBefore;
        session.Log($"[调试] stage1 汇总：单元 +{unitDelta}｜真实网络请求 +{requestDelta}｜{session.Budget.Describe()}");

        if (unitDelta != 8)
        {
            session.Violations.Add($"stage1: 真实翻译单元数 {unitDelta} != 8");
        }

        if (requestDelta <= 0)
        {
            session.Violations.Add($"stage1: 真实网络请求数 {requestDelta} <= 0（8 条必须真实到达 DeepSeek）");
        }

        if (requestDelta > SmokeBudget.MaxNetworkRequests)
        {
            session.Violations.Add($"stage1: 真实网络请求 {requestDelta} 超过上限 {SmokeBudget.MaxNetworkRequests}");
        }

        return results;
    }

    /// <summary>汇总：硬上限复核 + 四模式语义断言 + 生产安全前后对比 + 产物落盘。</summary>
    private static int FinishFourModes(
        SmokeSession session,
        IReadOnlyList<FourModeSample> samples,
        IReadOnlyList<SmokePhase> phases,
        IReadOnlyList<SmokeStageResult> stage,
        string runId,
        string projectRoot,
        string localizeRoot,
        ProductionSafetySnapshot safetyBefore,
        bool dryRun)
    {
        _ = phases;

        if (dryRun)
        {
            // dry-run：只报告矩阵/计划一致性，不做真实出网断言
            var dryFailures = session.Violations.Count;
            foreach (var violation in session.Violations)
            {
                session.Log($"[错误] {violation}");
            }

            session.Log($"[调试] dry-run 违规项：{dryFailures}（0 = 矩阵与计划一致，可执行真实 Smoke）");
            return dryFailures == 0 ? 0 : 1;
        }

        // ① 硬上限复核（代码层保护之外的最后一道核对）
        if (session.Budget.Units > SmokeBudget.MaxTranslationUnits)
        {
            session.Violations.Add($"真实翻译单元 {session.Budget.Units} 超过上限 {SmokeBudget.MaxTranslationUnits}");
        }

        if (session.Budget.Requests > SmokeBudget.MaxNetworkRequests)
        {
            session.Violations.Add($"真实网络请求 {session.Budget.Requests} 超过上限 {SmokeBudget.MaxNetworkRequests}");
        }

        if (session.Budget.ObservedRetryCount != 0)
        {
            session.Violations.Add($"观测到客户端重试 {session.Budget.ObservedRetryCount} 次（本轮要求 0）");
        }

        // ② 逐条单元：必须真实出网、不得命中 TM / Cache、返回非空
        foreach (var result in stage)
        {
            foreach (var unit in result.Units)
            {
                if (!unit.NetworkCalled)
                {
                    session.Violations.Add($"[{result.TranslationMode}] {unit.UnitKey} 未真实出网（networkCalled=false）");
                }

                if (unit.CacheHit)
                {
                    session.Violations.Add($"[{result.TranslationMode}] {unit.UnitKey} 意外命中 RequestCache（首次运行必须 Miss）");
                }

                if (unit.TmMatchType != "None")
                {
                    session.Violations.Add($"[{result.TranslationMode}] {unit.UnitKey} 意外命中 TM（{unit.TmMatchType}）");
                }

                if (!unit.TranslationNonEmpty)
                {
                    session.Violations.Add($"[{result.TranslationMode}] {unit.UnitKey} 真实返回为空");
                }
            }

            CheckFourModeSemantics(result, session);
        }

        // ③ 生产安全复核（前后对比；任何变化都是违规）
        var safetyAfter = ProductionSafetyProbe.Capture(projectRoot, localizeRoot);
        var safetyDifferences = ProductionSafetyProbe.Compare(safetyBefore, safetyAfter);
        foreach (var difference in safetyDifferences)
        {
            session.Violations.Add($"生产数据发生变化：{difference}");
        }

        // ④ 产物落盘（项目约定位置 + TEMP 副本）
        var outputDirectory = Path.Combine(
            projectRoot, "data", "output", "real_api_smoke", $"round_9_0b_final_four_modes_{runId}");
        Directory.CreateDirectory(outputDirectory);
        var details = new
        {
            Round = "9.0B-final-four-modes",
            RunId = runId,
            TempRoot = session.TempRoot,
            CredentialDetected = true,
            Provider = DeepSeekTranslationProvider.ProviderName,
            Model = session.Options.Model,
            BaseUrlHost = new Uri(session.Options.ApiUrl).Host,
            HardLimits = new
            {
                MaxTranslationUnits = SmokeBudget.MaxTranslationUnits,
                MaxNetworkRequests = SmokeBudget.MaxNetworkRequests,
            },
            Budget = new
            {
                session.Budget.Units,
                session.Budget.Requests,
                session.Budget.ObservedRetryCount,
            },
            Samples = samples.Select(sample => new
            {
                sample.Label,
                sample.LogicalFile,
                sample.RecordId,
                sample.Field,
                sample.Korean,
                sample.English,
                sample.Japanese,
                Neighbors = sample.Neighbors.Select(neighbor => new
                {
                    neighbor.RecordId,
                    neighbor.Korean,
                    neighbor.English,
                    neighbor.Japanese,
                    neighbor.Model,
                }),
            }),
            Modes = stage,
            ProductionSafety = new { Before = safetyBefore, After = safetyAfter, Differences = safetyDifferences },
            Violations = session.Violations,
            Notes = session.Notes,
            Passed = session.Violations.Count == 0,
        };
        SmokeReportWriter.Save(Path.Combine(outputDirectory, "smoke_results.json"), details);
        SmokeReportWriter.Save(Path.Combine(session.TempRoot, "smoke_results.json"), details);

        var markdown = FourModeComparisonReport.Build(runId, session, samples, stage, safetyBefore, safetyAfter, safetyDifferences);
        File.WriteAllText(Path.Combine(outputDirectory, "four_mode_comparison.md"), markdown);
        File.WriteAllText(Path.Combine(session.TempRoot, "four_mode_comparison.md"), markdown);

        session.Log($"[调试] {session.Budget.Describe()}");
        session.Log($"[调试] 产物：{outputDirectory}\\four_mode_comparison.md｜smoke_results.json");
        session.Log($"[调试] 生产安全：TM / 快照 / output / 游戏目录 前后一致 = {safetyDifferences.Count == 0}");

        if (session.Violations.Count > 0)
        {
            session.Log($"[错误] 真实四模式 Smoke 存在 {session.Violations.Count} 项违规：");
            foreach (var violation in session.Violations.Take(30))
            {
                session.Log($"[错误]   {violation}");
            }

            session.Log("[错误] ===== 第9.0B 最终真实四模式 Smoke：FAILED =====");
            return 1;
        }

        session.Log("[调试] ===== 第9.0B 最终真实四模式 Smoke：PASSED =====");
        return 0;
    }

    /// <summary>四模式语义断言（Trace 模式码 / 生效语言 / Canonical 存在性 / Thinking / Validator 误报）。</summary>
    private static void CheckFourModeSemantics(SmokeStageResult result, SmokeSession session)
    {
        var mode = result.TranslationMode;
        foreach (var unit in result.Units)
        {
            if (unit.TraceTranslationMode != mode)
            {
                session.Violations.Add($"[{mode}] {unit.UnitKey} Trace translationMode={unit.TraceTranslationMode}（期望 {mode}）");
            }

            switch (mode)
            {
                case "en_only":
                    if (unit.CanonicalKoreanText is not null || unit.CanonicalKoreanSent)
                    {
                        session.Violations.Add($"[{mode}] {unit.UnitKey} EN_ONLY 真实请求不得携带韩文 Canonical");
                    }

                    if (unit.CanonicalKoreanPresent is not null || unit.CanonicalChanged is not null
                        || unit.EnglishChanged is not null || unit.JapaneseChanged is not null)
                    {
                        session.Violations.Add($"[{mode}] {unit.UnitKey} EN_ONLY 的 Canonical 相关 Trace 字段必须为 N/A(null)");
                    }

                    if (unit.EffectiveSourceLanguage != "en" || unit.SourceHashSalt is not null)
                    {
                        session.Violations.Add($"[{mode}] {unit.UnitKey} EN_ONLY 生效语言/盐异常（lang={unit.EffectiveSourceLanguage}／salt={(unit.SourceHashSalt is null ? "null" : "非空")}）");
                    }

                    break;
                case "kr_en":
                case "kr_jp":
                case "kr_only":
                    if (!unit.CanonicalKoreanSent || unit.CanonicalKoreanPresent != true)
                    {
                        session.Violations.Add($"[{mode}] {unit.UnitKey} KR 模式真实请求必须携带韩文 Canonical");
                    }

                    if (unit.SourceHashSalt is null)
                    {
                        session.Violations.Add($"[{mode}] {unit.UnitKey} KR 模式 Mode Salt 不得为 null");
                    }

                    var expectedLanguage = mode switch
                    {
                        "kr_en" => "en",
                        "kr_jp" => "ja",
                        _ => "ko",
                    };
                    if (unit.EffectiveSourceLanguage != expectedLanguage)
                    {
                        session.Violations.Add($"[{mode}] {unit.UnitKey} 生效语言={unit.EffectiveSourceLanguage}（期望 {expectedLanguage}）");
                    }

                    if (unit.ValidationIssueCodes.Contains(ValidationIssueCodes.SourceLanguageAnomaly))
                    {
                        session.Violations.Add($"[{mode}] {unit.UnitKey} 出现模式级误报 SOURCE_LANGUAGE_ANOMALY");
                    }

                    break;
            }

            // StoryData：所有模式都必须有邻接上下文，且 Thinking 必须 ON（StoryData 既有策略）
            if (unit.UnitKey.Contains("StoryData/", StringComparison.Ordinal))
            {
                if (unit.NeighborPrevious is null && unit.NeighborNext is null)
                {
                    session.Violations.Add($"[{mode}] {unit.UnitKey} StoryData 邻接上下文丢失");
                }

                if (unit.ThinkingEnabled != true)
                {
                    session.Violations.Add($"[{mode}] {unit.UnitKey} StoryData Thinking 未开启（reason={unit.ThinkingPolicyReason}）");
                }
            }

            if (mode == "kr_only" && unit.ThinkingEnabled != true)
            {
                session.Violations.Add($"[{mode}] {unit.UnitKey} KR_ONLY Thinking 必须 ON（reason={unit.ThinkingPolicyReason}）");
            }
        }
    }
}


