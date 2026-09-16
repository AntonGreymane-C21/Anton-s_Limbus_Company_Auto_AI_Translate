using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Infrastructure.Translation;

/// <summary>
/// TranslationAgent（文档 §9-§14）。
///
/// 职责：
///   读取自己的 StageTask → 提示空源文 → 查询 Translation Memory
///   → 未命中条目整体交给 Provider（由 Provider 按 batch 配置切分实际请求）
///   → Placeholder 校验 → 保存 Checkpoint → 返回 AgentExecutionResult
///
/// 规则：
///   - Agent 之间并发；同一 Agent 内对 Provider 的调用按 Stage 串行
///   - 【第7.5轮】Agent 不承担「Provider 请求批次」切分：那是 DeepSeekTranslationProvider 的唯一职责
///   - 禁止直接修改最终汉化文件
///   - 必须捕获异常（失败隔离，禁止一个 Agent 失败导致全局终止）
/// </summary>
public sealed class TranslationAgent
{
    private readonly ITranslationProvider _provider;
    private readonly RateLimitManager _rateLimit;
    private readonly SqliteTranslationMemory _memory;
    private readonly Action<string> _log;
    private readonly int _maxConcurrentAgents;
    private readonly int _activeAgents;
    private readonly ValidationPipeline _validation;
    private readonly TranslationCacheServices? _cacheServices;
    private readonly ITranslationContextBuilder? _contextBuilder;

    public TranslationAgent(
        ITranslationProvider provider,
        RateLimitManager rateLimit,
        SqliteTranslationMemory memory,
        Action<string>? log = null,
        int maxConcurrentAgents = 8,
        int activeAgents = 1,
        ValidationPipeline? validation = null,
        TranslationCacheServices? cacheServices = null,
        ITranslationContextBuilder? contextBuilder = null)
    {
        _provider = provider;
        _rateLimit = rateLimit;
        _memory = memory;
        _log = log ?? (_ => { });
        _maxConcurrentAgents = maxConcurrentAgents;
        _activeAgents = activeAgents;
        _validation = validation ?? new ValidationPipeline();
        _cacheServices = cacheServices;
        _contextBuilder = contextBuilder;
    }

    /// <summary>
    /// 执行一个 Stage（文件）的翻译任务。
    /// </summary>
    /// <param name="stageId">Stage 标识（相对文件路径）</param>
    /// <param name="entries">该文件下需要翻译的 Diff 条目</param>
    public async Task<AgentExecutionResult> ExecuteAsync(
        string stageId,
        IReadOnlyList<DiffEntry> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0)
        {
            return new AgentExecutionResult { StageId = stageId, IsSuccess = true, TranslatedCount = 0 };
        }

        // 并发槽位日志（可视化并发生效）
        _log($"[调试] Agent[{stageId}] 启动，并发槽位 {_activeAgents}/{_maxConcurrentAgents}");

        try
        {
            // 第7.5轮：Agent 不做任何 API 尺寸分批 —— 本 Stage 的全部待翻译条目一次性交给 Provider，
            // 「Provider Request Batch」只由 DeepSeekTranslationProvider 按 config 的 batch 段切分。
            var translated = 0;
            var needsReview = 0;
            var errorIssues = 0;
            var warningIssues = 0;
            var emptySourceSkipped = 0;
            var emptySourceInherited = 0;
            var passthroughCount = 0;

            cancellationToken.ThrowIfCancellationRequested();

            // 第7轮 fail-safe：null / 空 / 纯空白源文永远不调用 Provider，
            // 也不写 Translation Memory / request_cache / Trace。
            // 空源文判定统一走 SourceTextGuard（禁止在各层散落 IsNullOrWhiteSpace）。
            //   - 旧中文非空：保留继承语义（禁止把已有旧中文清空）
            //   - 旧中文为空：译文维持空串（Merge 需要该 UnitKey 存在，因此写空串而不是 null）
            //
            // 第8.5轮：纯 Symbol / Punctuation（没有任何 Unicode 字母与数字、也没有占位符 / 标签字符）
            // 同样属于确定性系统行为 —— 直接把源文作为译文（TranslationSource.Passthrough），
            // 不调用 Provider、不写 TM / request_cache / Trace，但仍执行硬结构安全检查。
            var providerEntries = new List<DiffEntry>(entries.Count);
            foreach (var entry in entries)
            {
                if (!SourceTextGuard.IsReusable(entry.NewSourceText))
                {
                    entry.TmMatchType = TranslationMemoryMatchType.None;
                    entry.NeedsReview = false;
                    if (!string.IsNullOrWhiteSpace(entry.OldTranslation))
                    {
                        entry.Translation = entry.OldTranslation;
                        entry.Provenance = TranslationSource.Inherited;
                        emptySourceInherited++;
                    }
                    else
                    {
                        entry.Translation = string.Empty;
                        emptySourceSkipped++;
                    }

                    translated++;
                    continue;
                }

                if (SourceTextClassification.IsSymbolOnly(entry.NewSourceText))
                {
                    entry.Translation = entry.NewSourceText;
                    entry.Provenance = TranslationSource.Passthrough;
                    entry.TmMatchType = TranslationMemoryMatchType.None;
                    entry.ReviewReason = null;

                    // 结构安全不能成为后门：仍跑硬安全校验（Placeholder / Tag / Empty）
                    _validation.ValidateAndApply(entry);
                    if (entry.NeedsReview)
                    {
                        needsReview++;
                    }
                    errorIssues += entry.ValidationIssues.Count(i => i.Severity == ValidationSeverity.Error);
                    warningIssues += entry.ValidationIssues.Count(i => i.Severity == ValidationSeverity.Warning);

                    translated++;
                    passthroughCount++;
                    continue;
                }

                providerEntries.Add(entry);
            }

            // 查询 Translation Memory：只翻译未命中的
            // 第1轮：只有 UnitKey + SourceHash 同时一致（ExactUnit）才允许直接复用；
            //         仅 SourceHash 相同的跨 Unit 记录（CrossUnitSource）一律重新翻译。
            var missEntries = new List<DiffEntry>();
            var hitCount = 0;
            foreach (var entry in providerEntries)
            {
                var hash = SqliteTranslationMemory.ComputeSourceHash(entry.NewSourceText ?? string.Empty, entry.SourceHashSalt);
                var cached = _memory.FindExactUnit(entry.Key, hash);
                if (cached is not null)
                {
                    // TM 命中必须传播完整信息：译文、来源、NeedsReview、审核原因、命中级别
                    entry.Translation = cached.Translation;
                    entry.Provenance = cached.Source;
                    entry.NeedsReview = cached.NeedsReview;
                    entry.ReviewReason = cached.ReviewReason;
                    entry.TmMatchType = TranslationMemoryMatchType.ExactUnit;

                    // 第2轮：TM 命中也要经过当前 ValidatorPipeline（规则升级后仍能发现旧缓存问题），
                    //         但遵循来源感知策略（人工确认不会被普通 Warning 抹掉）。
                    _validation.ValidateAndApply(entry);
                    if (entry.NeedsReview)
                    {
                        needsReview++;
                    }
                    errorIssues += entry.ValidationIssues.Count(i => i.Severity == ValidationSeverity.Error);
                    warningIssues += entry.ValidationIssues.Count(i => i.Severity == ValidationSeverity.Warning);

                    translated++;
                    hitCount++;
                }
                else
                {
                    missEntries.Add(entry);
                }
            }

            if (hitCount > 0)
            {
                _log($"[调试] Agent[{stageId}] 命中 TM（ExactUnit）{hitCount} 条");
            }

            // 2) 通过 RateLimit 获取 API 名额，再调用翻译（一次调用覆盖本 Stage 全部未命中条目；
            //    实际网络请求条数由 Provider 按 batch 配置决定）
            if (missEntries.Count > 0)
            using (await _rateLimit.AcquireApiSlotAsync(cancellationToken))
            {
                _log($"[调试] Agent[{stageId}] 请求 Provider 翻译 {missEntries.Count} 条...");
                // 第5轮：为精确 TM 未命中的条目构造邻句上下文（来自运行前快照，纯查询）
                var contexts = _contextBuilder is null
                    ? null
                    : missEntries.ToDictionary(
                        e => e.Key.ToString(),
                        e => _contextBuilder.Build(e),
                        StringComparer.Ordinal);

                var results = await _provider.TranslateAsync(missEntries, cancellationToken, stageId, contexts);

                // 第5轮：暂存按 RequestId 隔离 —— 只收集本 Stage 自己那组请求
                // （Provider 内部可能产生多个请求，因此这里收集多个 RequestId）
                var requestIds = new List<string>();

                foreach (var entry in missEntries)
                {
                    if (results.TryGetValue(entry.Key.ToString(), out var result))
                    {
                        if (result.RequestId is not null && !requestIds.Contains(result.RequestId))
                        {
                            requestIds.Add(result.RequestId);
                        }
                        entry.Translation = result.Translation;
                        // 第1轮：Provider 结果的来源 / 审核原因必须传播到 DiffEntry（不再只复制译文）
                        entry.Provenance = result.Source;
                        entry.ReviewReason = result.ReviewReason;
                        entry.TmMatchType = TranslationMemoryMatchType.None;
                        if (result.NeedsReview)
                        {
                            entry.NeedsReview = true;
                        }

                        // 第2轮：Placeholder 恢复之后、TM 写入之前执行统一校验。
                        // Issues 与 NeedsReview 分层：Provider 已有的结构化 Issue 一并合并。
                        var validationReport = _validation.ValidateAndApply(entry, result.Issues);

                        // 第4轮：校验发现硬安全问题的请求，不允许写入可命中的 request_cache
                        //（只保留 Trace；下一次相同请求允许重新调用 Provider）
                        if (result.RequestId is not null
                            && validationReport.Issues.Any(i => i.Severity == ValidationSeverity.Error))
                        {
                            _cacheServices?.Staging?.Discard(result.RequestId);
                        }
                        translated++;
                        if (entry.NeedsReview)
                        {
                            needsReview++;
                        }
                        errorIssues += entry.ValidationIssues.Count(i => i.Severity == ValidationSeverity.Error);
                        warningIssues += entry.ValidationIssues.Count(i => i.Severity == ValidationSeverity.Warning);

                        // 3) 保存到 Translation Memory（Checkpoint，短事务）
                        try
                        {
                            var unit = TranslationUnitFactory.FromDiffEntry(entry);
                            // 第2轮：落库必须反映校验后的状态（含 NeedsReview / 审核原因），
                            //         否则下一次 ExactUnit 命中会丢失待审标记。
                            _memory.Save(unit, new TranslationResult
                            {
                                Key = result.Key,
                                Translation = result.Translation,
                                Source = result.Source,
                                NeedsReview = entry.NeedsReview,
                                ReviewReason = entry.ReviewReason,
                                TmMatchType = result.TmMatchType,
                                Issues = entry.ValidationIssues,
                            });
                        }
                        catch (Exception saveEx)
                        {
                            // TM 保存失败不影响主流程，仅记录
                            _log($"[调试] Agent[{stageId}] TM 保存失败: {saveEx.Message}");
                        }
                    }
                }

                // 第5轮：只提交本 Stage 自己那一组请求的暂存项（按 RequestId 隔离，互不影响）
                _cacheServices?.Staging?.Flush(requestIds);
            }

            if (errorIssues > 0 || warningIssues > 0)
            {
                _log($"[调试] Agent[{stageId}] 校验问题汇总: Error {errorIssues} 条 / Warning {warningIssues} 条");
            }

            if (emptySourceSkipped > 0 || emptySourceInherited > 0)
            {
                _log($"[调试] Agent[{stageId}] 空源文 {emptySourceSkipped + emptySourceInherited} 条：继承旧中文 {emptySourceInherited} 条，跳过翻译 {emptySourceSkipped} 条（未调用 Provider）");
            }

            if (passthroughCount > 0)
            {
                _log($"[调试] Agent[{stageId}] 纯符号文本 {passthroughCount} 条：原样保留（TranslationSource=Passthrough），未调用 Provider");
            }

            _log($"[调试] Agent[{stageId}] 完成: 翻译 {translated} 条, 需审核 {needsReview} 条");
            return new AgentExecutionResult
            {
                StageId = stageId,
                IsSuccess = true,
                TranslatedCount = translated,
                NeedsReviewCount = needsReview,
                TmHitCount = hitCount,
                PassthroughCount = passthroughCount,
                EmptySourceSkippedCount = emptySourceSkipped,
                EmptySourceInheritedCount = emptySourceInherited,
                ValidationErrorCount = errorIssues,
                ValidationWarningCount = warningIssues,
            };
        }
        catch (OperationCanceledException)
        {
            return new AgentExecutionResult
            {
                StageId = stageId,
                IsSuccess = false,
                Error = "已取消",
            };
        }
        catch (Exception ex)
        {
            // 失败隔离：捕获异常并返回失败结果
            _log($"[调试] Agent[{stageId}] 失败: {ex.Message}");
            return new AgentExecutionResult
            {
                StageId = stageId,
                IsSuccess = false,
                Error = ex.Message,
            };
        }
    }
}

