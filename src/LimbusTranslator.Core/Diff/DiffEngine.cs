using LimbusTranslator.Core.CharacterStyle;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Diff;

/// <summary>
/// 版本差异分析引擎。
///
/// 输入：
///   1. 旧版英文原文（OldEnglish）
///   2. 旧版中文汉化（OldChinese）
///   3. 新版英文原文（NewEnglish）
///
/// 输出：DiffResult（DiffEntry 列表 + 五类统计）
///
/// 核心原则：
///   新版英文决定输出结构，旧英文决定差异，旧中文决定继承风格。
/// </summary>
public sealed class DiffEngine
{
    /// <summary>
    /// 执行版本差异分析。
    /// </summary>
    /// <param name="oldEnglish">旧版英文 TranslationUnit 列表（文件路径带 EN_ 前缀）</param>
    /// <param name="oldChinese">旧版中文 TranslationUnit 列表（文件路径不带 EN_ 前缀）</param>
    /// <param name="newEnglish">新版英文 TranslationUnit 列表（文件路径带 EN_ 前缀）</param>
    public DiffResult Compute(
        IReadOnlyList<TranslationUnit> oldEnglish,
        IReadOnlyList<TranslationUnit> oldChinese,
        IReadOnlyList<TranslationUnit> newEnglish)
    {
        // 用 UnitKey 建索引，便于查找
        var oldEnglishMap = BuildMap(oldEnglish);
        var oldChineseMap = BuildMap(oldChinese);
        var newEnglishMap = BuildMap(newEnglish);
        var oldChineseSpeakerByRecord = BuildOldChineseSpeakerByRecord(oldChinese);

        var entries = new List<DiffEntry>();
        var newKeys = new HashSet<string>(newEnglishMap.Keys);

        // 按新版英文结构遍历（决定输出结构）
        foreach (var kvp in newEnglishMap)
        {
            var key = kvp.Key;
            var newUnit = kvp.Value;

            var oldEnExists = oldEnglishMap.TryGetValue(key, out var oldEnUnit);
            var oldZhExists = oldChineseMap.TryGetValue(key, out var oldZhUnit);

            var resolvedSpeaker = ResolveSpeaker(newUnit, oldChineseMap, oldChineseSpeakerByRecord);
            newUnit.Speaker = resolvedSpeaker;

            // 情况1：新增 ID（旧版英文中不存在）
            if (!oldEnExists)
            {
                entries.Add(new DiffEntry
                {
                    Key = newUnit.Key,
                    NewSourceText = newUnit.SourceText,
                    OldSourceText = null,
                    OldTranslation = null,
                    DiffKind = DiffKind.Added,
                    Action = TranslationAction.TranslateNew,
                    Speaker = resolvedSpeaker,
                    Order = newUnit.Order,
                });
                continue;
            }

            // 情况2：英文发生变化
            if (oldEnUnit!.SourceText != newUnit.SourceText)
            {
                entries.Add(new DiffEntry
                {
                    Key = newUnit.Key,
                    NewSourceText = newUnit.SourceText,
                    OldSourceText = oldEnUnit.SourceText,
                    // 旧中文文件的内容即旧译文（存于 SourceText 字段）
                    OldTranslation = oldZhExists ? oldZhUnit!.SourceText : null,
                    DiffKind = DiffKind.Modified,
                    Action = TranslationAction.TranslateModified,
                    Speaker = resolvedSpeaker,
                    Order = newUnit.Order,
                });
                continue;
            }

            // 情况3：英文没变，但旧中文缺失 → TranslateMissing
            if (!oldZhExists || string.IsNullOrEmpty(oldZhUnit!.SourceText))
            {
                entries.Add(new DiffEntry
                {
                    Key = newUnit.Key,
                    NewSourceText = newUnit.SourceText,
                    OldSourceText = oldEnUnit.SourceText,
                    OldTranslation = null,
                    DiffKind = DiffKind.Unchanged,
                    Action = TranslationAction.TranslateMissing,
                    Speaker = resolvedSpeaker,
                    Order = newUnit.Order,
                });
                continue;
            }

            // 情况4：英文没变，并且已有旧中文 → Inherit
            entries.Add(new DiffEntry
            {
                Key = newUnit.Key,
                NewSourceText = newUnit.SourceText,
                OldSourceText = oldEnUnit.SourceText,
                OldTranslation = oldZhUnit.SourceText,
                DiffKind = DiffKind.Unchanged,
                Action = TranslationAction.Inherit,
                Speaker = resolvedSpeaker,
                Order = newUnit.Order,
                Translation = oldZhUnit.SourceText,
                // 第1轮：继承来源标记（Provenance），便于端到端追踪译文来源
                Provenance = TranslationSource.Inherited,
            });
        }

        // 情况5：旧版存在但新版已删除 → Deleted（不输出）
        foreach (var key in oldEnglishMap.Keys)
        {
            if (!newKeys.Contains(key))
            {
                entries.Add(new DiffEntry
                {
                    Key = oldEnglishMap[key].Key,
                    NewSourceText = null,
                    OldSourceText = oldEnglishMap[key].SourceText,
                    OldTranslation = oldChineseMap.TryGetValue(key, out var zh)
                        ? zh.SourceText
                        : null,
                    DiffKind = DiffKind.Deleted,
                    Action = TranslationAction.SkipDeleted,
                });
            }
        }

        return new DiffResult
        {
            Entries = entries,
            // 第5轮：把完整新版英文单元一并返回（Context 索引复用，不重新扫描磁盘）
            NewUnits = newEnglish,
            // 第9.0B-P5轮：把本次已解析的旧中文单元原样带出（KR 权威模式按 UnitKey 取旧中文，不依赖英文 Diff 结构）
            OldChineseUnits = oldChinese,
            AddedCount = entries.Count(e => e.DiffKind == DiffKind.Added),
            ModifiedCount = entries.Count(e => e.DiffKind == DiffKind.Modified),
            UnchangedCount = entries.Count(e => e.DiffKind == DiffKind.Unchanged),
            DeletedCount = entries.Count(e => e.DiffKind == DiffKind.Deleted),
            MissingTranslationCount = entries.Count(e => e.Action == TranslationAction.TranslateMissing),
        };
    }

    /// <summary>
    /// 构建 UnitKey -> TranslationUnit 索引。
    /// </summary>
    private static Dictionary<string, TranslationUnit> BuildMap(IReadOnlyList<TranslationUnit> units)
    {
        var map = new Dictionary<string, TranslationUnit>(units.Count);
        foreach (var unit in units)
        {
            map[unit.Key.ToString()] = unit;
        }
        return map;
    }

    /// <summary>
    /// 构建旧中文 (RelativeFilePath, RecordId) -> Speaker/Teller 映射。
    /// 优先使用 .teller 字段的实际译文，其次使用非空的 unit.Speaker。
    /// </summary>
    private static Dictionary<(string RelativeFilePath, string RecordId), string> BuildOldChineseSpeakerByRecord(
        IReadOnlyList<TranslationUnit> oldChinese)
    {
        var map = new Dictionary<(string RelativeFilePath, string RecordId), string>();
        foreach (var unit in oldChinese)
        {
            if (string.IsNullOrEmpty(unit.Key.RelativeFilePath) || string.IsNullOrEmpty(unit.Key.RecordId))
            {
                continue;
            }

            var recordKey = (unit.Key.RelativeFilePath, unit.Key.RecordId);

            if (unit.Key.FieldPath.EndsWith(".teller", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(unit.SourceText))
            {
                map[recordKey] = unit.SourceText;
            }
            else if (!string.IsNullOrWhiteSpace(unit.Speaker) && !map.ContainsKey(recordKey))
            {
                map[recordKey] = unit.Speaker;
            }
        }
        return map;
    }

    /// <summary>
    /// 解析条目说话人：
    /// 1. 若 newUnit.Speaker 是罪人别名（韩文 model ID 或英文名），转换为中文名（如 그레고르 → 格里高尔）。
    /// 2. 若不是罪人别名，但 old_zh 中同文件同 recordId（或同 UnitKey）具有 teller / speaker（且非空），自动继承 old_zh 的中文 speaker（例如 유리 / Yuri → 尤莉）。
    /// 3. 其余保留原 newUnit.Speaker。
    /// </summary>
    private static string? ResolveSpeaker(
        TranslationUnit newUnit,
        Dictionary<string, TranslationUnit> oldChineseMap,
        Dictionary<(string RelativeFilePath, string RecordId), string> oldChineseSpeakerByRecord)
    {
        // 1. 若 newUnit.Speaker 是罪人别名（韩文 model ID 或英文名），转换为中文名
        if (CharacterAliasHelper.TryGetChineseName(newUnit.Speaker, out var sinnerChineseName))
        {
            return sinnerChineseName;
        }

        // 2. 若不是罪人别名，但 old_zh 中同文件同 recordId（或同 UnitKey）具有 teller / speaker（且非空），自动继承 old_zh 的中文 speaker
        string? candidateSpeaker = null;
        if (oldChineseMap.TryGetValue(newUnit.Key.ToString(), out var oldZhUnit)
            && !string.IsNullOrWhiteSpace(oldZhUnit.Speaker))
        {
            candidateSpeaker = oldZhUnit.Speaker;
        }
        else if (oldChineseSpeakerByRecord.TryGetValue((newUnit.Key.RelativeFilePath, newUnit.Key.RecordId), out var recordSpeaker)
                 && !string.IsNullOrWhiteSpace(recordSpeaker))
        {
            candidateSpeaker = recordSpeaker;
        }

        if (!string.IsNullOrWhiteSpace(candidateSpeaker))
        {
            return CharacterAliasHelper.TryGetChineseName(candidateSpeaker, out var zh)
                ? zh
                : candidateSpeaker;
        }

        // 3. 兜底返回 newUnit.Speaker
        return newUnit.Speaker;
    }
}
