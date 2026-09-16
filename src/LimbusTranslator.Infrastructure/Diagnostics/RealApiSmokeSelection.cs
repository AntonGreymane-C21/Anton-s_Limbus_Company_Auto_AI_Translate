using System.Text.Json;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Placeholder;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Infrastructure.Diagnostics;

/// <summary>
/// 第8轮真实 API 冒烟的确定性选择器。
///
/// 只选择正常、非空、不过长的待翻译条目；目标数始终限制在 10~30。
/// 该类不访问数据库、不访问网络、不写入游戏目录。
/// </summary>
public static class RealApiSmokeSelector
{
    public const int MinimumItemCount = 10;
    public const int MaximumItemCount = 30;
    public const int DefaultItemCount = 20;
    public const int DefaultMaxCharactersPerItem = 4_000;

    public static RealApiSmokeSelection Select(
        IReadOnlyList<DiffEntry> allEntries,
        ITranslationContextBuilder contextBuilder,
        IReadOnlyDictionary<string, GlossaryEntry> glossary,
        int targetItemCount = DefaultItemCount,
        int maxCharactersPerItem = DefaultMaxCharactersPerItem)
    {
        if (targetItemCount < MinimumItemCount || targetItemCount > MaximumItemCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetItemCount),
                $"真实 API 冒烟条目数必须在 {MinimumItemCount} ~ {MaximumItemCount} 之间。");
        }
        if (maxCharactersPerItem <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharactersPerItem));
        }

        var ordered = allEntries
            .Where(IsTranslatableAction)
            .OrderBy(entry => entry.Key.RelativeFilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Order)
            .ThenBy(entry => entry.Key.RecordId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Key.FieldPath, StringComparer.Ordinal)
            .ToList();

        var usable = ordered
            .Where(entry => SourceTextGuard.IsReusable(entry.NewSourceText))
            .Where(entry => BatchOptions.MeasureItemCharacters(entry) <= maxCharactersPerItem)
            .ToList();

        var contexts = usable.ToDictionary(
            entry => entry.Key.ToString(),
            contextBuilder.Build,
            StringComparer.Ordinal);
        var protector = new PlaceholderProtector();
        var lockedTerms = glossary
            .Where(pair => pair.Value.Locked && !string.IsNullOrWhiteSpace(pair.Key))
            .Select(pair => pair.Key)
            .OrderByDescending(term => term.Length)
            .ToArray();

        var selected = new List<DiffEntry>(targetItemCount);
        var selectedKeys = new HashSet<string>(StringComparer.Ordinal);
        var selectedStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(DiffEntry? candidate)
        {
            if (candidate is null || selected.Count >= targetItemCount || !selectedKeys.Add(candidate.Key.ToString()))
            {
                return;
            }

            selected.Add(candidate);
            selectedStages.Add(candidate.Key.RelativeFilePath);
        }

        DiffEntry? First(Func<DiffEntry, bool> predicate)
            => usable.FirstOrDefault(entry => !selectedKeys.Contains(entry.Key.ToString()) && predicate(entry));

        bool IsMiddleStory(DiffEntry entry)
        {
            var context = contexts[entry.Key.ToString()];
            return context.Previous is not null && context.Next is not null;
        }

        bool HasPlaceholder(DiffEntry entry)
            => protector.Protect(entry.NewSourceText ?? string.Empty).OriginalPlaceholders.Count > 0;

        bool HitsLockedGlossary(DiffEntry entry)
            => lockedTerms.Any(term => (entry.NewSourceText ?? string.Empty).Contains(term, StringComparison.OrdinalIgnoreCase));

        // 优先保证本轮希望覆盖的真实场景；某种场景不存在时不伪造数据。
        Add(First(IsMiddleStory));
        Add(First(entry => entry.Action == TranslationAction.TranslateModified
                           && !string.IsNullOrWhiteSpace(entry.OldSourceText)
                           && !string.IsNullOrWhiteSpace(entry.OldTranslation)));
        Add(First(HasPlaceholder));
        Add(First(HitsLockedGlossary));
        Add(First(entry => !IsStoryData(entry)));
        Add(First(entry => entry.Action == TranslationAction.TranslateNew));
        Add(First(entry => entry.Action == TranslationAction.TranslateMissing));

        // 先填充已选 Stage，减少小样本被拆成大量跨文件请求的概率。
        foreach (var entry in usable.Where(entry => selectedStages.Contains(entry.Key.RelativeFilePath)))
        {
            Add(entry);
        }
        foreach (var entry in usable)
        {
            Add(entry);
        }

        var selectedContexts = selected.ToDictionary(
            entry => entry.Key.ToString(),
            entry => contexts[entry.Key.ToString()],
            StringComparer.Ordinal);

        return new RealApiSmokeSelection
        {
            Entries = selected,
            Contexts = selectedContexts,
            CandidateCount = ordered.Count,
            UsableCandidateCount = usable.Count,
            EmptySourceExcludedCount = ordered.Count - ordered.Count(entry => SourceTextGuard.IsReusable(entry.NewSourceText)),
            TooLongExcludedCount = ordered.Count(entry => SourceTextGuard.IsReusable(entry.NewSourceText)
                                                    && BatchOptions.MeasureItemCharacters(entry) > maxCharactersPerItem),
            TargetItemCount = targetItemCount,
            MaxCharactersPerItem = maxCharactersPerItem,
            HasModifiedWithOldTexts = selected.Any(entry => entry.Action == TranslationAction.TranslateModified
                                                             && !string.IsNullOrWhiteSpace(entry.OldSourceText)
                                                             && !string.IsNullOrWhiteSpace(entry.OldTranslation)),
            HasPlaceholder = selected.Any(HasPlaceholder),
            HasLockedGlossary = selected.Any(HitsLockedGlossary),
            HasStoryMiddleSentence = selected.Any(IsMiddleStory),
            HasNonStoryData = selected.Any(entry => !IsStoryData(entry)),
        };
    }

    /// <summary>从固定计划恢复同一组 UnitKey，并验证 SourceHash 未变化。</summary>
    public static RealApiSmokeSelection ResolvePlan(
        RealApiSmokePlan plan,
        IReadOnlyList<DiffEntry> allEntries,
        ITranslationContextBuilder contextBuilder)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var byKey = allEntries.ToDictionary(entry => entry.Key.ToString(), StringComparer.Ordinal);
        var selected = new List<DiffEntry>(plan.Items.Count);
        var contexts = new Dictionary<string, TranslationContext>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
        {
            if (!byKey.TryGetValue(item.UnitKey, out var entry))
            {
                throw new InvalidOperationException($"[错误] 冒烟计划中的 UnitKey 已不在当前 Diff 中: {item.UnitKey}");
            }
            if (!IsTranslatableAction(entry) || !SourceTextGuard.IsReusable(entry.NewSourceText))
            {
                throw new InvalidOperationException($"[错误] 冒烟计划条目已不再是可安全翻译的正常 Source: {item.UnitKey}");
            }
            var hash = Persistence.SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText!);
            if (!string.Equals(hash, item.SourceHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"[错误] 冒烟计划条目的 SourceHash 已变化，拒绝复用旧样本: {item.UnitKey}");
            }

            selected.Add(entry);
            contexts[entry.Key.ToString()] = contextBuilder.Build(entry);
        }

        if (selected.Count < MinimumItemCount || selected.Count > MaximumItemCount)
        {
            throw new InvalidOperationException($"[错误] 冒烟计划条目数异常: {selected.Count}。");
        }

        return new RealApiSmokeSelection
        {
            Entries = selected,
            Contexts = contexts,
            CandidateCount = allEntries.Count(IsTranslatableAction),
            UsableCandidateCount = selected.Count,
            EmptySourceExcludedCount = 0,
            TooLongExcludedCount = 0,
            TargetItemCount = selected.Count,
            MaxCharactersPerItem = plan.MaxCharactersPerItem,
            HasModifiedWithOldTexts = selected.Any(entry => entry.Action == TranslationAction.TranslateModified
                                                             && !string.IsNullOrWhiteSpace(entry.OldSourceText)
                                                             && !string.IsNullOrWhiteSpace(entry.OldTranslation)),
            HasPlaceholder = selected.Any(entry => new PlaceholderProtector().Protect(entry.NewSourceText ?? string.Empty).OriginalPlaceholders.Count > 0),
            HasLockedGlossary = plan.HasLockedGlossary,
            HasStoryMiddleSentence = selected.Any(entry => contexts[entry.Key.ToString()].Previous is not null
                                                           && contexts[entry.Key.ToString()].Next is not null),
            HasNonStoryData = selected.Any(entry => !IsStoryData(entry)),
        };
    }

    public static RealApiSmokePlan CreatePlan(
        string runId,
        RealApiSmokeSelection selection,
        BatchOptions batchOptions)
    {
        var batches = selection.BuildPredictedProviderBatches(batchOptions);
        return new RealApiSmokePlan
        {
            RunId = runId,
            CreatedAtUtc = DateTime.UtcNow,
            ItemCount = selection.Entries.Count,
            ExpectedProviderBatchCount = batches.Count,
            MaxCharactersPerItem = selection.MaxCharactersPerItem,
            BatchMaxItemsPerBatch = batchOptions.MaxItemsPerBatch,
            BatchMaxCharactersPerBatch = batchOptions.MaxCharactersPerBatch,
            HasLockedGlossary = selection.HasLockedGlossary,
            Items = selection.Entries.Select(entry => new RealApiSmokePlanItem
            {
                UnitKey = entry.Key.ToString(),
                SourceHash = Persistence.SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText ?? string.Empty),
                Action = entry.Action.ToString(),
                RelativeFilePath = entry.Key.RelativeFilePath,
            }).ToArray(),
        };
    }

    public static void SavePlan(string path, RealApiSmokePlan plan)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(plan, JsonOptions));
    }

    public static RealApiSmokePlan LoadPlan(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("[错误] 找不到真实 API 冒烟计划。", path);
        }

        var plan = JsonSerializer.Deserialize<RealApiSmokePlan>(File.ReadAllText(path), JsonOptions);
        if (plan is null || string.IsNullOrWhiteSpace(plan.RunId) || plan.Items.Count < MinimumItemCount || plan.Items.Count > MaximumItemCount)
        {
            throw new InvalidOperationException("[错误] 真实 API 冒烟计划格式无效或条目数不在安全范围内。");
        }

        return plan;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// 第9.0B-P4轮：动作过滤统一走共享谓词（与 WPF / CLI / 生产链同源），不再自己复制一份。
    /// </summary>
    private static bool IsTranslatableAction(DiffEntry entry)
        => ProductionTranslationPlanBuilder.IsTranslationRequired(entry);

    private static bool IsStoryData(DiffEntry entry)
        => entry.Key.RelativeFilePath.StartsWith("StoryData/", StringComparison.OrdinalIgnoreCase);
}

/// <summary>受控冒烟选择结果；不含原文与译文的持久化内容。</summary>
public sealed class RealApiSmokeSelection
{
    public required IReadOnlyList<DiffEntry> Entries { get; init; }
    public required IReadOnlyDictionary<string, TranslationContext> Contexts { get; init; }
    public int CandidateCount { get; init; }
    public int UsableCandidateCount { get; init; }
    public int EmptySourceExcludedCount { get; init; }
    public int TooLongExcludedCount { get; init; }
    public int TargetItemCount { get; init; }
    public int MaxCharactersPerItem { get; init; }
    public bool HasModifiedWithOldTexts { get; init; }
    public bool HasPlaceholder { get; init; }
    public bool HasLockedGlossary { get; init; }
    public bool HasStoryMiddleSentence { get; init; }
    public bool HasNonStoryData { get; init; }

    public IReadOnlyList<IReadOnlyList<DiffEntry>> BuildPredictedProviderBatches(BatchOptions batchOptions)
        => Entries
            .GroupBy(entry => entry.Key.RelativeFilePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => ProviderBatchBuilder.Build(group.ToList(), batchOptions))
            .ToList();

    public IReadOnlyDictionary<string, string> BuildUnitKeyToSourceHash()
        => Entries.ToDictionary(
            entry => entry.Key.ToString(),
            entry => Persistence.SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText ?? string.Empty),
            StringComparer.Ordinal);
}

/// <summary>可持久化的同一批真实冒烟样本计划；只记录 UnitKey 与 SourceHash。</summary>
public sealed class RealApiSmokePlan
{
    public required string RunId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public int ItemCount { get; init; }
    public int ExpectedProviderBatchCount { get; init; }
    public int MaxCharactersPerItem { get; init; }
    public int BatchMaxItemsPerBatch { get; init; }
    public int BatchMaxCharactersPerBatch { get; init; }
    public bool HasLockedGlossary { get; init; }
    public required IReadOnlyList<RealApiSmokePlanItem> Items { get; init; }
}

/// <summary>冒烟计划中的单条脱敏标识。</summary>
public sealed class RealApiSmokePlanItem
{
    public required string UnitKey { get; init; }
    public required string SourceHash { get; init; }
    public required string Action { get; init; }
    public required string RelativeFilePath { get; init; }
}
