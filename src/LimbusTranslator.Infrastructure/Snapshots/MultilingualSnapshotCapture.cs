using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Multilingual;
using LimbusTranslator.Infrastructure.Parsing;

namespace LimbusTranslator.Infrastructure.Snapshots;

/// <summary>
/// 三语 Snapshot 捕获 + Canonical Diff 接线（第9.0A.1轮）：Analyze 主流程调用一次。
///
/// ① 解析 KR / EN / JP（EN 可复用调用方已解析结果，避免重复扫描）
/// ② 复用 <see cref="MultilingualAlignmentService"/> 对齐
/// ③ 读上一份三语快照 → <see cref="CanonicalDiffService"/>（**韩文决定**）
/// ④ 成功后按 KR → EN → JP 保存 baseline（缺目录 / 空单元跳过，不伪造）
///
/// KR 缺失 ⇒ 直接失败（**不得退回 EN 作为 Canonical**），且不写任何快照。
/// </summary>
public static class MultilingualSnapshotCapture
{
    /// <summary>KR 目录缺失 / 解析不到 Canonical 单元时的失败原因</summary>
    public const string MissingKoreanReason = "未找到韩文原文目录，无法执行 Canonical Diff。";

    /// <summary>执行捕获。<paramref name="languageDirectories"/> 为「语言 → 物理目录」（null/不存在视为缺失）。</summary>
    /// <param name="projectRoot">读取上一份快照的根目录（默认也是写入根）</param>
    /// <param name="languageDirectories">三语物理目录</param>
    /// <param name="configDir">字段规则目录</param>
    /// <param name="log">日志回调</param>
    /// <param name="preParsedEnglish">调用方已解析的英文单元（复用，避免二次解析）</param>
    /// <param name="snapshotRoot">
    /// 第9.0C.1轮：**三语快照读写根**（默认 = <paramref name="projectRoot"/>）。
    /// 用于 Demo / TEMP 工作区隔离：演示数据绝不写生产 <c>data/cache/source_snapshots</c>。
    /// </param>
    /// <param name="cancellationToken">取消令牌（在语言/文件粒度检查；取消时不写任何快照）</param>
    public static MultilingualCaptureResult Capture(
        string projectRoot,
        IReadOnlyDictionary<SourceLanguage, string?> languageDirectories,
        string? configDir = null,
        Action<string>? log = null,
        IReadOnlyList<TranslationUnit>? preParsedEnglish = null,
        string? snapshotRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(languageDirectories);

        var effectiveSnapshotRoot = string.IsNullOrWhiteSpace(snapshotRoot) ? projectRoot : snapshotRoot!;
        var parser = new JsonGameFileParser(configDir);
        var units = new Dictionary<SourceLanguage, List<TranslationUnit>>();
        var currentCounts = new Dictionary<SourceLanguage, int>();
        var scannedFiles = new Dictionary<SourceLanguage, IReadOnlyList<string>>();
        var missing = new List<SourceLanguage>();

        foreach (var language in SourceLanguageHelper.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            languageDirectories.TryGetValue(language, out var directory);

            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                missing.Add(language);
                currentCounts[language] = 0;
                continue;
            }

            if (language == SourceLanguage.English && preParsedEnglish is not null)
            {
                units[language] = preParsedEnglish.ToList();
                // 第9.0B-P7轮：调用方已解析英文单元时，仍枚举一次文件集合（只枚举不解析，成本极低）
                scannedFiles[language] = LanguageFileMapper.ScanPhysicalFiles(directory!)
                    .Select(LanguageFileMapper.ToCanonicalRelativePath)
                    .ToList();
            }
            else
            {
                units[language] = ParseDirectory(parser, language, directory!, out var logicalFiles, cancellationToken);
                scannedFiles[language] = logicalFiles;
            }

            currentCounts[language] = units[language].Count;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // KR 是 Canonical：缺失即失败，且不写任何快照（不得退回 EN）
        if (!units.TryGetValue(SourceLanguage.Korean, out var korean) || korean.Count == 0)
        {
            log?.Invoke("[错误] " + MissingKoreanReason + "（本次不更新任何语言的 Snapshot）");
            return new MultilingualCaptureResult
            {
                Success = false,
                FailureReason = MissingKoreanReason,
                CurrentUnitCounts = currentCounts,
                MissingDirectories = missing,
                ScannedLogicalFiles = scannedFiles,
            };
        }

        units.TryGetValue(SourceLanguage.English, out var english);
        units.TryGetValue(SourceLanguage.Japanese, out var japanese);

        // ② 对齐（UnitKey 统一入口，不手工 Join）
        var alignment = MultilingualAlignmentService.Align(korean, english, japanese, SourceLanguage.English);

        // ③ 读取上一份三语快照 + Canonical Diff
        var previousKorean = SourceSnapshotService.TryLoadCurrent(effectiveSnapshotRoot, SourceLanguage.Korean);
        var previousEnglish = SourceSnapshotService.TryLoadCurrent(effectiveSnapshotRoot, SourceLanguage.English);
        var previousJapanese = SourceSnapshotService.TryLoadCurrent(effectiveSnapshotRoot, SourceLanguage.Japanese);

        var previousCounts = new Dictionary<SourceLanguage, int>
        {
            [SourceLanguage.Korean] = previousKorean?.Units.Count ?? -1,
            [SourceLanguage.English] = previousEnglish?.Units.Count ?? -1,
            [SourceLanguage.Japanese] = previousJapanese?.Units.Count ?? -1,
        };

        var canonical = CanonicalDiffService.Compute(
            previousKorean is null ? null : SourceSnapshotService.ToTranslationUnits(previousKorean),
            korean,
            previousEnglish is null ? null : SourceSnapshotService.ToTranslationUnits(previousEnglish),
            english,
            previousJapanese is null ? null : SourceSnapshotService.ToTranslationUnits(previousJapanese),
            japanese);

        var isFirst = previousKorean is null;
        WriteDiagnostics(log, currentCounts, previousCounts, canonical, isFirst, missing);

        // ④ 保存（第9.0C.1轮：**三语事务式一起提交**；取消时不写任何快照，baseline 保持原状）
        var saved = new List<SourceLanguage>();
        var failures = new List<SourceLanguage>();
        cancellationToken.ThrowIfCancellationRequested();

        var batch = SourceLanguageHelper.All
            .Where(language => units.TryGetValue(language, out var list) && list.Count > 0)
            .Select(language => (Language: language, Units: (IReadOnlyList<TranslationUnit>)units[language], SourceRoot: languageDirectories[language]))
            .ToList();

        try
        {
            saved.AddRange(SourceSnapshotService.SaveBaselineBatch(effectiveSnapshotRoot, batch, log));
            foreach (var language in saved)
            {
                log?.Invoke($"[调试] {SourceLanguageHelper.GetDisplayName(language)} Snapshot 已更新。");
            }
        }
        catch (Exception ex)
        {
            failures.AddRange(batch.Select(item => item.Language));
            log?.Invoke($"[错误] 三语 Snapshot 保存失败：{ex.Message}（已保留全部语言的旧快照）");
        }

        return new MultilingualCaptureResult
        {
            Success = true,
            CurrentUnitCounts = currentCounts,
            PreviousUnitCounts = previousCounts,
            SavedLanguages = saved,
            MissingDirectories = missing,
            SaveFailures = failures,
            CanonicalDiff = canonical,
            Alignment = alignment.Stats,
            IsFirstCanonicalBaseline = isFirst,
            Sources = BuildSources(units),
            ScannedLogicalFiles = scannedFiles,
            ParsedUnits = units.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TranslationUnit>)pair.Value,
                EqualityComparer<SourceLanguage>.Default),
            PreviousKoreanSources = previousKorean is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : previousKorean.Units.ToDictionary(u => u.RelativeFilePath + "|" + u.RecordId + "|" + u.FieldPath, u => u.SourceText, StringComparer.Ordinal),
        };
    }

    /// <summary>把三语解析结果整理成 UnitKey → 三语文本（第9.0B收官轮）。</summary>
    private static IReadOnlyDictionary<string, MultilingualUnitSources> BuildSources(
        Dictionary<SourceLanguage, List<TranslationUnit>> units)
    {
        // 第9.0C.1轮（性能修复）：原实现按 Key 对每个语言列表线性查找（FirstOrDefault），
        // 真实数据（≈15 万 Key × 3 语）下是 O(n²)，会让分析在「三语捕获」之后卡住十几分钟。
        // 改为每种语言先建 Key → 文本字典（O(n)），语义保持「首次出现优先」（与 FirstOrDefault 一致）。
        var lookups = new Dictionary<SourceLanguage, Dictionary<string, string>>(units.Count);
        foreach (var (language, list) in units)
        {
            var lookup = new Dictionary<string, string>(list.Count, StringComparer.Ordinal);
            foreach (var unit in list)
            {
                lookup.TryAdd(unit.Key.ToString(), unit.SourceText);
            }

            lookups[language] = lookup;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lookup in lookups.Values)
        {
            foreach (var key in lookup.Keys)
            {
                keys.Add(key);
            }
        }

        var map = new Dictionary<string, MultilingualUnitSources>(keys.Count, StringComparer.Ordinal);
        foreach (var key in keys)
        {
            map[key] = new MultilingualUnitSources
            {
                Korean = TextOf(lookups, SourceLanguage.Korean, key),
                English = TextOf(lookups, SourceLanguage.English, key),
                Japanese = TextOf(lookups, SourceLanguage.Japanese, key),
            };
        }

        return map;
    }

    private static string? TextOf(
        Dictionary<SourceLanguage, Dictionary<string, string>> lookups,
        SourceLanguage language,
        string key)
        => lookups.TryGetValue(language, out var lookup) && lookup.TryGetValue(key, out var text) ? text : null;

    /// <summary>
    /// 第9.0B收官轮（P0）：把三语源文、Mode Salt 与 Canonical 动作**真正写入生产条目**。
    ///
    /// 行为：
    ///   1. EN_ONLY：不改动任何字段（NewSourceText/OldSourceText 保持旧英文语义），
    ///      CanonicalKoreanText/OldCanonicalKoreanText/SourceHashSalt 全部保持 null；
    ///   2. KR_EN / KR_JP：Canonical=KR、Selected=EN/JP（缺失则 Fallback→KR 且 Selected 改为 KR）；
    ///   3. KR_ONLY：Canonical=Selected=KR（只一份）；缺 KR 时不调用 Provider（有旧中文→Inherit，否则 SkipDeleted + NeedsReview）；
    ///   4. 三个 KR 模式：Action 由 <see cref="CanonicalActionPlanner"/> 决定（首次基线走迁移模式，绝不产生 TranslateNew/TranslateModified）；
    ///   5. 三个 KR 模式：SourceHashSalt = <see cref="TranslationModePolicy.BuildModeSalt"/>（EN_ONLY 为 null → 旧 TM 兼容）。
    /// </summary>
    /// <returns>被写入（或按 Canonical 调整过动作）的条目数</returns>
    public static int ApplyToEntries(
        IEnumerable<DiffEntry> entries,
        MultilingualCaptureResult capture,
        TranslationMode mode,
        IReadOnlyCollection<string>? oldChineseUnitKeys = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(capture);

        var list = entries.ToList();
        if (!TranslationModePolicy.UsesCanonicalKoreanDiff(mode))
        {
            return 0; // EN_ONLY：保持纯英文语义，不注入任何 Canonical 字段
        }

        var oldChinese = new HashSet<string>(oldChineseUnitKeys ?? Array.Empty<string>(), StringComparer.Ordinal);
        var plan = capture.CanonicalDiff is null
            ? null
            : CanonicalActionPlanner.Plan(capture.CanonicalDiff, oldChinese, capture.IsFirstCanonicalBaseline);

        var patched = 0;
        // 第9.0B-P1轮：参考译本变化集合（仅诊断 / Trace；不参与动作判定）
        var englishChangedKeys = new HashSet<string>(
            (capture.CanonicalDiff?.EnglishChanges ?? Array.Empty<ReferenceChange>()).Select(change => change.UnitKey),
            StringComparer.Ordinal);
        var japaneseChangedKeys = new HashSet<string>(
            (capture.CanonicalDiff?.JapaneseChanges ?? Array.Empty<ReferenceChange>()).Select(change => change.UnitKey),
            StringComparer.Ordinal);

        foreach (var entry in list)
        {
            var key = entry.Key.ToString();
            capture.Sources.TryGetValue(key, out var sources);
            capture.PreviousKoreanSources.TryGetValue(key, out var oldKorean);

            var korean = sources?.Korean;
            var selected = mode switch
            {
                TranslationMode.KoreanOnly => sources?.Korean,
                TranslationMode.KoreanJapanese => sources?.Japanese,
                _ => sources?.English,
            };

            var effective = mode == TranslationMode.KoreanOnly
                ? SourceLanguage.Korean
                : mode == TranslationMode.KoreanJapanese
                    ? SourceLanguage.Japanese
                    : SourceLanguage.English;

            if (string.IsNullOrWhiteSpace(selected))
            {
                // KR_EN / KR_JP 允许回退韩文；KR_ONLY 本身就是韩文
                if (!string.IsNullOrWhiteSpace(korean))
                {
                    selected = korean;
                    effective = SourceLanguage.Korean;
                }
            }

            // ⓪ 语言元数据（第9.0B-P1轮）：Validator（语言感知）/ Thinking（模式感知）的唯一事实来源。
            //    只写「模式 + 生效语言」这类元数据，不涉及任何 Canonical 源文内容（EN_ONLY 根本不进入本方法）。
            entry.RunTranslationMode = mode;
            entry.EffectiveSourceLanguage = effective;
            entry.EnglishReferenceChanged = englishChangedKeys.Contains(key);
            entry.JapaneseReferenceChanged = japaneseChangedKeys.Contains(key);

            // ① 三语字段
            entry.CanonicalKoreanText = string.IsNullOrWhiteSpace(korean) ? null : korean;
            entry.OldCanonicalKoreanText = string.IsNullOrWhiteSpace(oldKorean) ? null : oldKorean;
            entry.SourceHashSalt = TranslationModePolicy.BuildModeSalt(mode, effective, entry.CanonicalKoreanText);

            // ② Selected Source（NewSourceText/OldSourceText 即生产链的 Source）
            if (!string.IsNullOrWhiteSpace(selected))
            {
                entry.NewSourceText = selected;
            }

            // ③ KR_ONLY 缺 KR：禁止调用 Provider
            if (mode == TranslationMode.KoreanOnly && entry.CanonicalKoreanText is null)
            {
                entry.Action = oldChinese.Contains(key) ? TranslationAction.Inherit : TranslationAction.SkipDeleted;
                entry.NeedsReview = true;
                entry.ReviewReason = "CANONICAL_KOREAN_SOURCE_MISSING";
                patched++;
                continue;
            }

            // ④ Canonical 动作（覆盖 EN Diff 动作）
            if (plan is not null && plan.Actions.TryGetValue(key, out var action))
            {
                entry.Action = action;
                patched++;
            }

            if (entry.CanonicalKoreanText is null)
            {
                entry.NeedsReview = true;
                entry.ReviewReason = "CANONICAL_KOREAN_SOURCE_MISSING";
            }
        }

        return patched;
    }

    /// <summary>KR_JP 的 Selected Source 取日文；其它情况取英文。</summary>
    private static bool IsKoreanJapanese(TranslationMode mode) => mode == TranslationMode.KoreanJapanese;

    private static List<TranslationUnit> ParseDirectory(JsonGameFileParser parser, SourceLanguage language, string directory)
    {
        return ParseDirectory(parser, language, directory, out _);
    }

    /// <summary>
    /// 解析某语言目录下的全部 JSON，并输出**扫描到的逻辑文件路径**（第9.0B-P7轮：
    /// 供 <c>OldChineseScopeLoader</c> 复用，避免二次遍历目录）。
    /// </summary>
    private static List<TranslationUnit> ParseDirectory(
        JsonGameFileParser parser,
        SourceLanguage language,
        string directory,
        out IReadOnlyList<string> scannedLogicalFiles,
        CancellationToken cancellationToken = default)
    {
        _ = language;
        var units = new List<TranslationUnit>();
        var files = new List<string>();
        foreach (var physical in LanguageFileMapper.ScanPhysicalFiles(directory))
        {
            // 第9.0C.1轮：文件粒度检查取消（真实 2000+ 文件时足以快速响应，不会拖慢正常解析）
            cancellationToken.ThrowIfCancellationRequested();
            var logical = LanguageFileMapper.ToCanonicalRelativePath(physical);
            var full = Path.Combine(directory, physical.Replace('/', Path.DirectorySeparatorChar));
            files.Add(logical);
            if (File.Exists(full))
            {
                units.AddRange(parser.Parse(full, logical));
            }
        }

        scannedLogicalFiles = files;
        return units;
    }

    private static void WriteDiagnostics(
        Action<string>? log,
        IReadOnlyDictionary<SourceLanguage, int> currentCounts,
        IReadOnlyDictionary<SourceLanguage, int> previousCounts,
        CanonicalDiffResult canonical,
        bool isFirst,
        IReadOnlyList<SourceLanguage> missing)
    {
        if (log is null)
        {
            return;
        }

        if (isFirst)
        {
            log("[调试] 未发现韩文原文快照，本次分析后建立 Canonical Baseline。");
        }

        foreach (var language in SourceLanguageHelper.All)
        {
            var name = language switch
            {
                SourceLanguage.Korean => "韩文 Canonical",
                SourceLanguage.Japanese => "日文参考",
                _ => "英文参考",
            };

            var current = currentCounts.TryGetValue(language, out var c) ? c.ToString() : "0";
            var previous = previousCounts.TryGetValue(language, out var p) && p >= 0 ? p.ToString() : "无";
            log($"[调试] {name}快照：current={current}｜previous={previous}");
        }

        foreach (var language in missing)
        {
            log($"[调试] 未发现{SourceLanguageHelper.GetDisplayName(language)}源目录，本次不更新{SourceLanguageHelper.GetDisplayName(language)} Snapshot。");
        }

        log($"[调试] {canonical.Describe()}");
        log($"[调试] 参考译本变化：EN {canonical.EnglishChangedCount}｜JP {canonical.JapaneseChangedCount}");
    }
}