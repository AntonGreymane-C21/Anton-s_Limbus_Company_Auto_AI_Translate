using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Infrastructure.Translation;

/// <summary>
/// Coordinator（文档 §9-§14）。
///
/// 职责：
///   1. 将 Diff 条目按文件（Stage）分组
///   2. 用 SemaphoreSlim 控制同时运行的 Agent 数（MaxConcurrentAgents）
///   3. 每个 Agent 失败隔离，汇总 CoordinatorResult
///   4. 完成后返回可复用的译文集合（供 Merge）
///
/// 并发模型：Agent 之间并发，Agent 内 Batch 串行。
/// </summary>
public sealed class Coordinator
{
    private readonly ITranslationProvider _provider;
    private readonly SqliteTranslationMemory _memory;
    private readonly RateLimitManager _rateLimit;
    private readonly Action<string> _log;
    private readonly ValidationPipeline? _validation;
    private readonly TranslationCacheServices? _cacheServices;
    private readonly ITranslationContextBuilder? _contextBuilder;

    public Coordinator(
        ITranslationProvider provider,
        SqliteTranslationMemory memory,
        int maxConcurrentAgents = 8,
        int maxConcurrentApiRequests = 10,
        Action<string>? log = null,
        ValidationPipeline? validation = null,
        TranslationCacheServices? cacheServices = null,
        ITranslationContextBuilder? contextBuilder = null)
    {
        _provider = provider;
        _memory = memory;
        _rateLimit = new RateLimitManager(maxConcurrentAgents, maxConcurrentApiRequests);
        _log = log ?? (_ => { });
        _validation = validation;
        _cacheServices = cacheServices;
        _contextBuilder = contextBuilder;
    }

    /// <summary>
    /// 执行完整翻译流程。
    /// </summary>
    /// <param name="entries">需要翻译的 Diff 条目（TranslateNew/Modified/Missing）</param>
    /// <param name="progress">进度回调（完成数/总数/Stage 名）</param>
    public async Task<CoordinatorResult> ExecuteAsync(
        IReadOnlyList<DiffEntry> entries,
        Action<int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // 1) 按文件分组为 Stage
        var stages = entries
            .GroupBy(e => e.Key.RelativeFilePath)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Stage: g.Key, Entries: g.ToList()))
            .ToList();

        var results = new List<AgentExecutionResult>();
        var completed = 0;
        var total = stages.Count;

        // 2) 用 SemaphoreSlim 并发执行 Agent
        // 第8.75轮：翻译开始前输出一次 Thinking 策略统计（自适应策略下 ON/OFF 会混合）
        LogThinkingPolicyStatistics(entries);

        using var semaphore = new SemaphoreSlim(_rateLimit.GetMaxConcurrentAgents());
        var tasks = new List<Task>();
        var activeCount = 0;

        foreach (var stage in stages)
        {
            await semaphore.WaitAsync(cancellationToken);
            activeCount++;
            var currentActive = activeCount;
            var maxAgents = _rateLimit.GetMaxConcurrentAgents();
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var agent = new TranslationAgent(_provider, _rateLimit, _memory, _log,
                        maxConcurrentAgents: maxAgents,
                        activeAgents: currentActive,
                        validation: _validation,
                        cacheServices: _cacheServices,
                        contextBuilder: _contextBuilder);
                    var result = await agent.ExecuteAsync(stage.Stage, stage.Entries, cancellationToken);

                    lock (results)
                    {
                        results.Add(result);
                        completed++;
                        progress?.Invoke(completed, total, stage.Stage);
                    }
                }
                finally
                {
                    semaphore.Release();
                    lock (results)
                    {
                        activeCount--;
                    }
                }
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);

        return new CoordinatorResult
        {
            Agents = results,
        };
    }

    /// <summary>
    /// 输出一次 Thinking 策略统计（第8.75轮）：
    /// 只统计 ON/OFF 与原因码数量，不逐条打印；Provider 未实现 <see cref="IThinkingDecisionSource"/> 时跳过。
    /// </summary>
    private void LogThinkingPolicyStatistics(IReadOnlyList<DiffEntry> entries)
    {
        if (_provider is not IThinkingDecisionSource source || entries.Count == 0)
        {
            return;
        }

        var onByAnomaly = 0;
        var onByStory = 0;
        var onByOther = 0;
        var off = 0;
        foreach (var entry in entries)
        {
            if (!source.IsThinkingEnabled(entry))
            {
                off++;
                continue;
            }

            var reason = source.ResolveThinkingReason(entry);
            if (reason == ThinkingPolicyReasons.SourceLanguageAnomaly)
            {
                onByAnomaly++;
            }
            else if (reason == ThinkingPolicyReasons.StoryData)
            {
                onByStory++;
            }
            else
            {
                onByOther++;
            }
        }

        _log(
            $"[调试] Thinking策略统计：ON（源语言异常）= {onByAnomaly}；ON（StoryData）= {onByStory}"
            + (onByOther > 0 ? $"；ON（其它）= {onByOther}" : string.Empty)
            + $"；OFF（普通文本）= {off}");
    }

    /// <summary>
    /// 收集所有已翻译条目的译文映射（供 Merge 使用）。
    /// </summary>
    public static IReadOnlyDictionary<string, string> CollectTranslations(IReadOnlyList<DiffEntry> entries)
    {
        var dict = new Dictionary<string, string>();
        foreach (var entry in entries)
        {
            if (entry.Translation is not null && entry.Action != TranslationAction.SkipDeleted)
            {
                dict[entry.Key.ToString()] = entry.Translation;
            }
        }
        return dict;
    }
}
