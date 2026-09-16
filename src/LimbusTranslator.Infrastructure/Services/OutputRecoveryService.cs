using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 从翻译缓存恢复输出时的结果。
/// </summary>
public sealed class OutputRecoveryResult
{
    public required IReadOnlyDictionary<string, string> Translations { get; init; }
    public required IReadOnlyList<DiffEntry> MissingEntries { get; init; }
    public int InheritedOrInMemoryCount { get; init; }
    public int CacheHitCount { get; init; }
}

/// <summary>
/// 从 TranslationMemory 收集当前英文版本可复用的译文。
/// 第1轮规则：只有 UnitKey + SourceHash 同时一致（ExactUnit）才复用，
/// 绝不跨 Unit 复用，也不把过期缓存写入输出；不会调用 API。
/// </summary>
public static class OutputRecoveryService
{
    public static OutputRecoveryResult Collect(
        IReadOnlyList<DiffEntry> entries,
        SqliteTranslationMemory translationMemory,
        ValidationPipeline? validation = null)
    {
        var translations = new Dictionary<string, string>();
        var missingEntries = new List<DiffEntry>();
        var inMemoryCount = 0;
        var cacheHitCount = 0;

        foreach (var entry in entries.Where(entry => entry.Action != TranslationAction.SkipDeleted))
        {
            var key = entry.Key.ToString();
            if (entry.Translation is not null)
            {
                translations[key] = entry.Translation;
                inMemoryCount++;

                // 第2轮：内存中已有的译文（如 Inherit 旧中文）同样经过统一校验。
                // Provenance=Inherited → 只跑硬结构安全检查，不会因启发式产生海量 NeedsReview。
                validation?.ValidateAndApply(entry);
                continue;
            }

            var sourceText = entry.NewSourceText ?? string.Empty;
            var sourceHash = SqliteTranslationMemory.ComputeSourceHash(sourceText);
            var cached = translationMemory.FindExactUnit(entry.Key, sourceHash);
            if (cached is null)
            {
                missingEntries.Add(entry);
                continue;
            }

            // 命中必须传播完整信息（含 NeedsReview / 审核原因 / 来源 / 命中级别）
            entry.Translation = cached.Translation;
            entry.NeedsReview = cached.NeedsReview;
            entry.ReviewReason = cached.ReviewReason;
            entry.Provenance = cached.Source;
            entry.TmMatchType = TranslationMemoryMatchType.ExactUnit;
            translations[key] = cached.Translation;
            cacheHitCount++;

            // 第2轮：TM 命中也要经过当前 ValidatorPipeline（规则升级后可发现旧缓存的结构问题）
            validation?.ValidateAndApply(entry);
        }

        return new OutputRecoveryResult
        {
            Translations = translations,
            MissingEntries = missingEntries,
            InheritedOrInMemoryCount = inMemoryCount,
            CacheHitCount = cacheHitCount,
        };
    }
}
