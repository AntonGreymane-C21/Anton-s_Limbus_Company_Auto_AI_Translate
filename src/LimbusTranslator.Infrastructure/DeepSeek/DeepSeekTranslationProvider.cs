using System.Diagnostics;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.CharacterStyle;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Placeholder;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// DeepSeek 翻译提供者。
///
/// 实现 ITranslationProvider，负责：
///   - 将 Diff 条目划分为 Batch（Provider 侧 Batch = 最终实际请求的粒度）
///   - Placeholder 保护（模型的输入）
///   - 动态术语选择 / 动态角色风格（稳定排序，保证同等语义 → 相同请求指纹）
///   - RequestFingerprint + request_cache 查询与暂存（第4轮）
///   - 通过 <see cref="IDeepSeekBatchClient"/> 调用 API（可注入 Fake，禁止在测试里访问真实 API）
///   - Trace（脱敏：只记 Hash 与元数据）
///   - 将结果转换为 TranslationResult（含 RequestFingerprint / CacheHit / RequestId）
///
/// 【缓存边界】TM 命中（ExactUnit）在本层之前完成，不会进入 request_cache。
/// 【资源边界】本类实现 IDisposable：自己创建的客户端与限流器由本类释放，注入的客户端由调用方释放。
/// </summary>
public sealed class DeepSeekTranslationProvider : ITranslationProvider, IThinkingDecisionSource, ILockedTerminologyRepairProvider, IDisposable
{
    /// <summary>Provider 标识（进入指纹与 Trace）</summary>
    public const string ProviderName = "deepseek";

    private readonly DeepSeekOptions _options;
    private readonly IDeepSeekBatchClient _client;
    private readonly bool _ownsClient;
    private readonly BatchOptions _batchOptions;
    private readonly TranslationThinkingPolicy _thinkingPolicy;
    private readonly SemaphoreSlim _rateLimiter;
    private readonly GlossaryService? _glossary;

    /// <summary>第8.875轮：本次运行固定的术语快照（Prompt 与 Validator 同源；运行中不可变）。</summary>
    private readonly ActiveGlossarySnapshot? _glossarySnapshot;

    /// <summary>第9.0B.3A轮：本次 Run 锁定的翻译模式（由 Run 初始化传入，Provider **不重新读配置**）。</summary>
    private readonly TranslationMode _translationMode;
    private readonly CharacterStyleService? _characterStyles;
    private readonly PromptOptions _prompt;
    private readonly PlaceholderProtector _protector = new();
    private readonly TranslationCacheServices? _cacheServices;
    private readonly Action<string> _log;
    private int _disposedFlag;

    /// <summary>
    /// 构造翻译提供者。
    /// </summary>
    /// <param name="options">DeepSeek 配置</param>
    /// <param name="configDir">配置目录（用于加载 glossary.json，可为空）</param>
    /// <param name="batchOptions">Provider 请求分批配置（第7.5轮：本层是唯一分批来源；null = 默认 20 条 / 30000 字符）</param>
    /// <param name="cacheServices">缓存 / Trace 服务（null = 完全关闭）</param>
    /// <param name="client">批量调用客户端（null = 使用真实 DeepSeekClient；测试注入 Fake）</param>
    /// <param name="log">调试日志回调</param>
    public DeepSeekTranslationProvider(
        DeepSeekOptions options,
        string? configDir = null,
        BatchOptions? batchOptions = null,
        TranslationCacheServices? cacheServices = null,
        IDeepSeekBatchClient? client = null,
        Action<string>? log = null,
        TranslationThinkingPolicy? thinkingPolicy = null,
        ActiveGlossarySnapshot? glossarySnapshot = null,
        TranslationMode translationMode = TranslationMode.EnglishOnly)
    {
        _options = options;
        // 加载提示词配置（config/prompt.json）
        _prompt = PromptLoader.Load(configDir);
        _client = client ?? new DeepSeekClient(options, _prompt);
        _ownsClient = client is null;
        _cacheServices = cacheServices;
        _log = log ?? (_ => { });
        _batchOptions = batchOptions ?? BatchOptions.Default;
        // 第8.75轮：Thinking 决策唯一来源（未注入时按 config 的 thinkingMode / reasoningEffort 建立）
        _thinkingPolicy = thinkingPolicy ?? TranslationThinkingPolicy.FromOptions(options);
        _rateLimiter = new SemaphoreSlim(
            options.MaxConcurrentRequests > 0 ? options.MaxConcurrentRequests : 10);

        // 第9.0B.3A轮 / 第9.0B-P2修复：本次 Run 锁定的「术语快照」与「翻译模式」与 configDir 是否存在无关，
        // 必须在任何情况下赋值。
        // 修复前这两行被误放进下面的 `if (!string.IsNullOrEmpty(configDir))` 分支：
        //   configDir 为空时 _translationMode 退回默认值 EN_ONLY ⇒ 请求体不带韩文区块、
        //   SourceModeCode 恒为 en_only ⇒ 四模式 RequestCache 互相命中（E2E 实测指纹与 KR_EN 完全相同）。
        _glossarySnapshot = glossarySnapshot;
        _translationMode = translationMode;

        // 加载术语库 + 角色风格（文件不存在时对应字段为 null）
        if (!string.IsNullOrEmpty(configDir))
        {
            _glossary = new GlossaryService(configDir);
            _characterStyles = new CharacterStyleService(configDir);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
        IReadOnlyList<DiffEntry> entries,
        CancellationToken cancellationToken = default,
        string? stageId = null,
        IReadOnlyDictionary<string, TranslationContext>? contexts = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);

        // 第7轮 fail-safe（第二道防线）：空源文条目永不生成 Provider 请求。
        // TranslationAgent 已在队列入口拦截；这里保证任何直接调用 Provider 的路径同样安全
        // （不进入 Batch、不写 request_cache、不写 Trace）。
        var results = new Dictionary<string, TranslationResult>();
        var translatable = new List<DiffEntry>(entries.Count);
        var skippedEmptySource = 0;
        foreach (var entry in entries)
        {
            if (SourceTextGuard.IsReusable(entry.NewSourceText))
            {
                translatable.Add(entry);
            }
            else
            {
                skippedEmptySource++;
            }
        }

        if (skippedEmptySource > 0)
        {
            _log($"[调试] 空源文条目 {skippedEmptySource} 条：不生成 Provider 请求（不调用 API，不写 request_cache / Trace）");
        }

        // 第8.75轮：先按 Thinking 决策分组 —— 相同 Thinking（含 reasoningEffort）才能进入同一个 Provider Request Batch，
        // 然后才在组内按 MaxItems / MaxCharacters 切分（第7.5轮规则不变）。
        // 分组键顺序 = 首次出现顺序；组内顺序 = 原顺序 ⇒ 确定性，不依赖线程/字典枚举顺序。
        var thinkingGroups = new List<(ThinkingDecision Decision, List<DiffEntry> Entries)>();
        foreach (var entry in translatable)
        {
            var decision = _thinkingPolicy.Decide(entry);
            var index = thinkingGroups.FindIndex(g =>
                g.Decision.Enabled == decision.Enabled
                && string.Equals(g.Decision.ReasoningEffort, decision.ReasoningEffort, StringComparison.Ordinal));
            if (index < 0)
            {
                thinkingGroups.Add((decision, new List<DiffEntry> { entry }));
            }
            else
            {
                thinkingGroups[index].Entries.Add(entry);
            }
        }

        if (thinkingGroups.Count > 1)
        {
            _log(
                $"[调试] Thinking 分组：{thinkingGroups.Count} 组（"
                + string.Join("，", thinkingGroups.Select(g =>
                    $"{(g.Decision.Enabled ? "ON" : "OFF")}/{g.Decision.Reason}={g.Entries.Count}")) + "）");
        }

        var batchIndex = 0;
        foreach (var group in thinkingGroups)
        {
            var decisions = group.Decision;
            var batches = ProviderBatchBuilder.Build(group.Entries, _batchOptions, _log);

        foreach (var batch in batches)
        {
            batchIndex++;
            var batchId = $"Batch{batchIndex:000}";
            var requestId = _cacheServices?.NextRequestId() ?? $"req-local-{batchIndex:D5}";
            var startedAt = Stopwatch.StartNew();

            await _rateLimiter.WaitAsync(cancellationToken);
            try
            {
                // 1) Placeholder 保护：把“模型实际看到的字段”固定下来（缓存与指纹都以它为准）
                var protectedMap = new Dictionary<string, PlaceholderProtectedText>(StringComparer.Ordinal);
                var requestItems = new List<DeepSeekTranslateRequestItem>(batch.Count);
                foreach (var entry in batch)
                {
                    var unitKey = entry.Key.ToString();
                    var protectedSource = _protector.Protect(entry.NewSourceText ?? string.Empty);
                    protectedMap[unitKey] = protectedSource;

                    requestItems.Add(new DeepSeekTranslateRequestItem
                    {
                        Id = unitKey,
                        Source = protectedSource.ProtectedText,
                        OldSource = entry.OldSourceText is null
                            ? null
                            : _protector.Protect(entry.OldSourceText).ProtectedText,
                        OldTranslation = entry.OldTranslation is null
                            ? null
                            : _protector.Protect(entry.OldTranslation).ProtectedText,
                        Speaker = entry.Speaker,
                        // 第9.0B.3A轮：KR 模式把韩文原文写入请求项；EN_ONLY 保持 null（请求体不出现韩文）
                        CanonicalKorean = TranslationModePolicy.SendsKorean(_translationMode) && !string.IsNullOrWhiteSpace(entry.CanonicalKoreanText)
                            ? _protector.Protect(entry.CanonicalKoreanText!).ProtectedText
                            : null,
                        OldCanonicalKorean = TranslationModePolicy.SendsKorean(_translationMode) && !string.IsNullOrWhiteSpace(entry.OldCanonicalKoreanText)
                            ? _protector.Protect(entry.OldCanonicalKoreanText!).ProtectedText
                            : null,
                        // 第5轮：邻句上下文（来自运行前的 Context 索引快照）
                        Context = contexts is not null && contexts.TryGetValue(unitKey, out var context)
                            ? context
                            : null,
                    });
                }

                // 2) 动态术语选择 / 动态角色风格（内部稳定排序，保证语义等价 → 同一指纹）
                var glossaryPrompt = BuildGlossaryPromptForBatch(batch, out var matchedTerms);
                var characterStylePrompt = BuildCharacterStylePromptForBatch(batch);

                // 第8.87轮：把「旧译文规则」与「分类指令」也纳入真实请求
                //   - includeModifiedRule：本批存在旧英文/旧中文时生效（旧译文只作参考）
                //   - category：本批分类完全一致时才下发（混合批不下发，避免错误指令）
                var includeModifiedRule = batch.Any(e =>
                    !string.IsNullOrWhiteSpace(e.OldSourceText) || !string.IsNullOrWhiteSpace(e.OldTranslation));
                var category = ResolveUniformCategory(batch);

                // 3) 实际请求内容与请求指纹（与 DeepSeekClient 共用同一份构造逻辑，不做影子字段清单）
                var systemPrompt = DeepSeekRequestComposer.BuildSystemPrompt(
                    _prompt,
                    glossaryPrompt,
                    characterStylePrompt,
                    DeepSeekRequestComposer.ContainsContext(requestItems),
                    includeModifiedRule,
                    category);
                var userContent = DeepSeekRequestComposer.BuildUserContent(batchId, requestItems);

                var fingerprint = RequestFingerprintBuilder.Build(new RequestFingerprintPayload
                {
                    Provider = ProviderName,
                    ProviderIdentity = RequestFingerprintBuilder.SanitizeProviderIdentity(_options.ApiUrl),
                    Model = _options.Model,
                    Temperature = _options.Temperature,
                    MaxTokens = _options.MaxTokens,
                    ResponseFormat = DeepSeekRequestComposer.ResponseFormatType,
                    // 第8.5轮：思考模式真实进入请求，因此同样进入指纹（不手工拼 thinking）
                    // 第8.75轮：指纹使用**本批实际决策**（自适应策略下同一 Stage 可能 ON/OFF 混合）
                    Thinking = decisions.Enabled,
                    ReasoningEffort = decisions.Enabled ? decisions.ReasoningEffort : null,
                    SystemPrompt = systemPrompt,
                    UserContent = userContent,
                    GlossaryPrompt = glossaryPrompt,
                    CharacterStylePrompt = characterStylePrompt,

                    // 第9.0B-P1轮（Fingerprint v3）：四模式语义
                    // EN_ONLY：Canonical 三字段保持 null ⇒ 完全不受 KR 变化影响（纯英文缓存不会被无意义地失效）
                    SourceModeCode = TranslationModeCodes.ToCode(_translationMode),
                    EffectiveSourceLanguage = ResolveEffectiveLanguageCode(batch),
                    SelectedSourceText = requestItems.Count == 1 ? requestItems[0].Source : null,
                    OldSelectedSourceText = requestItems.Count == 1 ? requestItems[0].OldSource : null,
                    UsedKoreanFallback = TranslationModePolicy.SendsKorean(_translationMode)
                                         && batch.Any(e => !string.IsNullOrWhiteSpace(e.CanonicalKoreanText)
                                                           && string.Equals(e.NewSourceText, e.CanonicalKoreanText, StringComparison.Ordinal)),
                    CanonicalKoreanText = TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode)
                        ? string.Join("\n", batch.Select(e => e.CanonicalKoreanText ?? string.Empty))
                        : null,
                    OldCanonicalKoreanText = TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode)
                        ? string.Join("\n", batch.Select(e => e.OldCanonicalKoreanText ?? string.Empty))
                        : null,
                    CanonicalChanged = TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode)
                        ? batch.Any(e => !string.Equals(e.CanonicalKoreanText ?? string.Empty, e.OldCanonicalKoreanText ?? string.Empty, StringComparison.Ordinal))
                        : null,
                    Items = requestItems
                        .Select((item, index) => new RequestFingerprintItem
                        {
                            Id = DeepSeekResponseParser.EncodeId(item.Id),
                            UnitKey = item.Id,
                            TranslationMode = batch[index].Action.ToString(),
                            Source = item.Source,
                            OldSource = item.OldSource,
                            OldTranslation = item.OldTranslation,
                            Speaker = item.Speaker,
                            // 第5轮：邻句上下文的确定性 canonical 片段（与真实请求中的 context 完全一致）
                            Context = DeepSeekRequestComposer.BuildContextJson(item.Context),
                        })
                        .ToArray(),
                });

                // 4) request_cache 查询（缓存只能在 Provider 层，DeepSeekClient 保持纯协议层）
                CachedProviderBatchResponse? cached = null;
                if (_cacheServices?.Cache is not null)
                {
                    cached = _cacheServices.Cache.TryGet(fingerprint.Value);

                    // 结构校验：缓存的 ID 集合必须与本次请求完全一致（Missing / Extra / Duplicate → 视为无用缓存）
                    if (cached is not null && !IsCacheUsable(cached, requestItems))
                    {
                        _log($"[调试] RequestCache 记录无效（ID 集合不一致），按 Cache Miss 处理: Fingerprint={fingerprint.ShortValue}");
                        cached = null;
                    }

                    _log(cached is null
                        ? $"[调试] RequestCache Miss: Fingerprint={fingerprint.ShortValue}"
                        : $"[调试] RequestCache Hit: Fingerprint={fingerprint.ShortValue}");
                }

                // 5) 命中 → 重放缓存响应；未命中 → 调用 Provider
                var cacheHit = cached is not null;
                var batchResult = cacheHit
                    ? ReplayCached(cached!)
                    : await _client.TranslateBatchWithMetadataAsync(
                        batchId, requestItems, cancellationToken, glossaryPrompt, characterStylePrompt,
                        new DeepSeekRequestThinking(decisions.Enabled, decisions.Enabled ? decisions.ReasoningEffort : null),
                        includeModifiedRule,
                        category,
                        _translationMode);

                // 6) 与网络响应完全相同的后处理链（Cache Hit 不允许短路）
                var placeholderFailed = false;
                var cacheItems = new List<CachedProviderItem>(batchResult.Items.Count);

                foreach (var item in batchResult.Items.Values)
                {
                    var issues = new List<ValidationIssue>();

                    var needsReview = item.NeedsReview;
                    string? reviewReason = string.IsNullOrEmpty(item.Reason) ? null : item.Reason;

                    // 恢复 Placeholder：严格校验失败时宽容恢复并标记待审核
                    string restored;
                    if (protectedMap.TryGetValue(item.Id, out var protectedSource))
                    {
                        var lenient = _protector.RestoreLenient(item.Translation, protectedSource);
                        restored = lenient.Text;
                        if (!lenient.Validation.IsValid)
                        {
                            needsReview = true;
                            reviewReason = $"Placeholder 校验异常: {lenient.Validation.Describe()}";
                            placeholderFailed = true;
                            // 第2轮：宽容恢复也要保留结构化 Issue（Pipeline 会合并进报告）
                            issues.Add(new ValidationIssue
                            {
                                Key = ParseKey(item.Id),
                                Code = ValidationIssueCodes.PlaceholderMismatch,
                                Severity = ValidationSeverity.Error,
                                Category = ValidationCategory.Placeholder,
                                Validator = nameof(PlaceholderProtector),
                                Message = $"Placeholder 宽容恢复: {lenient.Validation.Describe()}",
                            });
                        }
                    }
                    else
                    {
                        restored = item.Translation;
                    }

                    // 缓存保存的是“模型原始输出”（未恢复）；Cache Hit 后仍走上面的恢复链
                    cacheItems.Add(new CachedProviderItem
                    {
                        Id = item.Id,
                        Translation = item.Translation,
                        NeedsReview = item.NeedsReview,
                        Reason = item.Reason,
                    });

                    results[item.Id] = new TranslationResult
                    {
                        Key = ParseKey(item.Id),
                        Translation = restored,
                        Source = TranslationSource.AI,
                        NeedsReview = needsReview,
                        ReviewReason = reviewReason,
                        Issues = issues,
                        RequestFingerprint = fingerprint.Value,
                        CacheHit = cacheHit,
                        RequestId = requestId,
                    };
                }

                // 7) 只有“新请求 + Placeholder 校验通过”才暂存缓存（按 RequestId 隔离）；
                //    落库由 TranslationAgent 在当前 Validator 通过后 Flush（硬安全问题不写缓存）
                if (!cacheHit && !placeholderFailed && _cacheServices?.Staging is not null)
                {
                    _cacheServices.Staging.Stage(requestId, fingerprint.Value, new CachedProviderBatchResponse
                    {
                        FormatVersion = CachedProviderBatchResponse.CurrentFormatVersion,
                        Provider = ProviderName,
                        Model = _options.Model,
                        CreatedAtUtc = DateTime.UtcNow,
                        ResponseId = batchResult.ResponseId,
                        ResponseModel = batchResult.ResponseModel,
                        PromptTokens = batchResult.PromptTokens,
                        CompletionTokens = batchResult.CompletionTokens,
                        TotalTokens = batchResult.TotalTokens,
                        ReasoningTokens = batchResult.ReasoningTokens,
                        Items = cacheItems,
                    });
                }

                // 8) Trace（脱敏：只有 Hash / 元数据 / 计数 / 命中术语名）
                startedAt.Stop();
                WriteTrace(
                    stageId, batchId, requestId, requestItems, fingerprint,
                    decisions, cacheHit, batchResult, startedAt.ElapsedMilliseconds, success: true, failure: null,
                    glossaryTerms: matchedTerms, batchContext: batch);
            }
            catch (Exception ex)
            {
                startedAt.Stop();
                WriteTrace(
                    stageId, batchId, requestId, null, null,
                    decisions, cacheHit: false, batchResult: null, startedAt.ElapsedMilliseconds,
                    success: false, failure: ex, fallbackItemCount: batch.Count,
                    fallbackUnitKeys: batch.Select(e => e.Key.ToString()).ToArray(), batchContext: batch);
                throw;
            }
            finally
            {
                _rateLimiter.Release();
            }
        }
        }

        return results;
    }

    /// <summary>
    // ───────── 第9.0C.2轮：锁定术语修正（独立请求种类 + 独立指纹） ─────────

    /// <summary>
    /// 针对单条条目执行一次锁定术语修正请求（**不做自由翻译**）。
    ///
    /// 与常规翻译的差异（仅以下各项）：
    ///   1. 请求种类 locked_terminology_repair ⇒ 独立指纹，不与翻译请求互相命中；
    ///   2. 请求体额外携带 CurrentTranslation（当前译文）与 MustUseTerms（锁定术语清单）；
    ///   3. System Prompt 追加修正规则（DeepSeekRequestComposer.RepairRule）；
    ///   4. 不注入术语表 / 角色风格段落（锁定术语已显式列出），不携带邻句上下文；
    ///   5. Thinking 强制关闭（确定性局部校正，节省 Token）。
    ///
    /// 四模式语义保持不变：Selected Source 与 CanonicalKorean 的取值规则与常规翻译完全一致，
    /// 且 TranslationMode 不变（Trace 仍记录原模式）。
    /// </summary>
    public async Task<LockedTerminologyRepairResult> RepairLockedTerminologyAsync(
        DiffEntry entry,
        IReadOnlyList<TerminologyRequirement> lockedTerms,
        string currentTranslation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var terms = (lockedTerms ?? Array.Empty<TerminologyRequirement>())
            .Where(t => t.Locked && !t.PreservedAsEnglish && !string.IsNullOrWhiteSpace(t.Target))
            .GroupBy(t => t.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(t => t.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (terms.Count == 0)
        {
            return new LockedTerminologyRepairResult { Attempted = false, SkipReason = "无锁定术语" };
        }

        if (string.IsNullOrWhiteSpace(currentTranslation))
        {
            return new LockedTerminologyRepairResult { Attempted = false, SkipReason = "当前译文为空" };
        }

        if (!SourceTextGuard.IsReusable(entry.NewSourceText))
        {
            return new LockedTerminologyRepairResult { Attempted = false, SkipReason = "源文不可用" };
        }

        var unitKey = entry.Key.ToString();
        var lockedTermsText = string.Join("\n", terms.Select(t => $"{t.Source} → {t.Target}"));
        var protectedSource = _protector.Protect(entry.NewSourceText!);
        var protectedCurrent = _protector.Protect(currentTranslation);

        var item = new DeepSeekTranslateRequestItem
        {
            Id = unitKey,
            Source = protectedSource.ProtectedText,
            OldSource = entry.OldSourceText is null ? null : _protector.Protect(entry.OldSourceText).ProtectedText,
            OldTranslation = entry.OldTranslation is null ? null : _protector.Protect(entry.OldTranslation).ProtectedText,
            Speaker = entry.Speaker,
            CanonicalKorean = TranslationModePolicy.SendsKorean(_translationMode) && !string.IsNullOrWhiteSpace(entry.CanonicalKoreanText)
                ? _protector.Protect(entry.CanonicalKoreanText!).ProtectedText
                : null,
            OldCanonicalKorean = TranslationModePolicy.SendsKorean(_translationMode) && !string.IsNullOrWhiteSpace(entry.OldCanonicalKoreanText)
                ? _protector.Protect(entry.OldCanonicalKoreanText!).ProtectedText
                : null,
            CurrentTranslation = protectedCurrent.ProtectedText,
            LockedTerms = lockedTermsText,
        };

        var requestItems = new List<DeepSeekTranslateRequestItem> { item };
        var entryBatch = new List<DiffEntry> { entry };
        // 修正默认关闭 Thinking（确定性局部校正；原因码稳定进入 Trace）
        var decisions = new ThinkingDecision(false, LockedTerminologyCheck.RepairReasonKind);
        var batchId = "Repair001";   // 固定：batch_id 进入请求指纹，必须确定性
        var requestId = _cacheServices?.NextRequestId() ?? $"repair-local-{unitKey}";
        var startedAt = Stopwatch.StartNew();

        var systemPrompt = DeepSeekRequestComposer.BuildSystemPrompt(
            _prompt,
            string.Empty,
            string.Empty,
            includeContextRule: false,
            includeModifiedRule: false,
            category: null,
            mode: _translationMode,
            includeRepairRule: true);
        var userContent = DeepSeekRequestComposer.BuildUserContent(batchId, requestItems);

        var fingerprint = RequestFingerprintBuilder.Build(new RequestFingerprintPayload
        {
            Provider = ProviderName,
            ProviderIdentity = RequestFingerprintBuilder.SanitizeProviderIdentity(_options.ApiUrl),
            Model = _options.Model,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
            ResponseFormat = DeepSeekRequestComposer.ResponseFormatType,
            Thinking = decisions.Enabled,
            ReasoningEffort = null,
            SystemPrompt = systemPrompt,
            UserContent = userContent,
            GlossaryPrompt = null,
            CharacterStylePrompt = null,
            SourceModeCode = TranslationModeCodes.ToCode(_translationMode),
            EffectiveSourceLanguage = ResolveEffectiveLanguageCode(entryBatch),
            SelectedSourceText = item.Source,
            OldSelectedSourceText = item.OldSource,
            CanonicalKoreanText = TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode) ? item.CanonicalKorean : null,
            OldCanonicalKoreanText = TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode) ? item.OldCanonicalKorean : null,
            // 修正请求独立字段：术语内容 / 当前译文 / 术语快照 Hash 全部进入指纹
            RequestKind = LockedTerminologyCheck.RepairReasonKind,
            RepairCurrentTranslation = protectedCurrent.ProtectedText,
            RepairLockedTerms = lockedTermsText,
            GlossarySnapshotHash = _glossarySnapshot?.SnapshotHash,
            Items = new[]
            {
                new RequestFingerprintItem
                {
                    Id = DeepSeekResponseParser.EncodeId(item.Id),
                    UnitKey = item.Id,
                    TranslationMode = entry.Action.ToString(),
                    Source = item.Source,
                    OldSource = item.OldSource,
                    OldTranslation = item.OldTranslation,
                    Speaker = item.Speaker,
                    Context = null,
                },
            },
        });

        CachedProviderBatchResponse? cached = null;
        if (_cacheServices?.Cache is not null)
        {
            cached = _cacheServices.Cache.TryGet(fingerprint.Value);
            if (cached is not null && !IsCacheUsable(cached, requestItems))
            {
                _log($"[调试] 锁定术语修正缓存无效（ID 集合不一致），按 Cache Miss 处理: Fingerprint={fingerprint.ShortValue}");
                cached = null;
            }
        }

        var cacheHit = cached is not null;
        try
        {
            await _rateLimiter.WaitAsync(cancellationToken);
            try
            {
                var batchResult = cacheHit
                    ? ReplayCached(cached!)
                    : await _client.TranslateBatchWithMetadataAsync(
                        batchId, requestItems, cancellationToken, string.Empty, string.Empty,
                        new DeepSeekRequestThinking(decisions.Enabled, decisions.Enabled ? decisions.ReasoningEffort : null),
                        includeModifiedRule: false,
                        category: null,
                        _translationMode);

                var placeholderFailed = false;
                var cacheItems = new List<CachedProviderItem>(batchResult.Items.Count);
                string? repaired = null;
                var needsReview = false;
                string? reviewReason = null;
                var issues = new List<ValidationIssue>();

                if (batchResult.Items.TryGetValue(unitKey, out var repairedItem))
                {
                    needsReview = repairedItem.NeedsReview;
                    reviewReason = string.IsNullOrEmpty(repairedItem.Reason) ? null : repairedItem.Reason;

                    // 与常规翻译完全相同的 Placeholder 恢复链（修正不得破坏占位符）
                    var lenient = _protector.RestoreLenient(repairedItem.Translation, protectedSource);
                    repaired = lenient.Text;
                    if (!lenient.Validation.IsValid)
                    {
                        needsReview = true;
                        reviewReason = $"Placeholder 校验异常: {lenient.Validation.Describe()}";
                        placeholderFailed = true;
                        issues.Add(new ValidationIssue
                        {
                            Key = entry.Key,
                            Code = ValidationIssueCodes.PlaceholderMismatch,
                            Severity = ValidationSeverity.Error,
                            Category = ValidationCategory.Placeholder,
                            Validator = nameof(PlaceholderProtector),
                            Message = $"Placeholder 宽容恢复: {lenient.Validation.Describe()}",
                        });
                    }

                    cacheItems.Add(new CachedProviderItem
                    {
                        Id = repairedItem.Id,
                        Translation = repairedItem.Translation,
                        NeedsReview = repairedItem.NeedsReview,
                        Reason = repairedItem.Reason,
                    });
                }
                else
                {
                    placeholderFailed = true;
                    reviewReason = "修正响应缺少该条目";
                }

                // 只有「新请求 + Placeholder 校验通过」才暂存缓存；落库由 Agent 在最终校验通过后 Flush
                if (!cacheHit && !placeholderFailed && _cacheServices?.Staging is not null)
                {
                    _cacheServices.Staging.Stage(requestId, fingerprint.Value, new CachedProviderBatchResponse
                    {
                        FormatVersion = CachedProviderBatchResponse.CurrentFormatVersion,
                        Provider = ProviderName,
                        Model = _options.Model,
                        CreatedAtUtc = DateTime.UtcNow,
                        ResponseId = batchResult.ResponseId,
                        ResponseModel = batchResult.ResponseModel,
                        PromptTokens = batchResult.PromptTokens,
                        CompletionTokens = batchResult.CompletionTokens,
                        TotalTokens = batchResult.TotalTokens,
                        ReasoningTokens = batchResult.ReasoningTokens,
                        Items = cacheItems,
                    });
                }

                _log(cacheHit
                    ? $"[调试] 锁定术语自动修正：缓存命中（未产生网络请求）｜UnitKey={unitKey}"
                    : $"[调试] 锁定术语自动修正：已请求模型｜UnitKey={unitKey}｜锁定期望 {terms.Count} 条｜指纹={fingerprint.ShortValue}");

                startedAt.Stop();
                WriteTrace(
                    stageId: entry.Key.RelativeFilePath,
                    batchId,
                    requestId,
                    requestItems,
                    fingerprint,
                    decisions,
                    cacheHit,
                    batchResult,
                    startedAt.ElapsedMilliseconds,
                    success: true,
                    failure: null,
                    fallbackItemCount: 0,
                    fallbackUnitKeys: null,
                    glossaryTerms: null,
                    batchContext: entryBatch);

                return new LockedTerminologyRepairResult
                {
                    Attempted = true,
                    Translation = repaired,
                    CacheHit = cacheHit,
                    RequestId = requestId,
                    NeedsReview = needsReview,
                    ReviewReason = reviewReason,
                    Issues = issues,
                    InputTokens = batchResult.PromptTokens,
                    OutputTokens = batchResult.CompletionTokens,
                    ReasoningTokens = batchResult.ReasoningTokens,
                    SkipReason = repaired is null ? "修正响应缺少条目" : null,
                };
            }
            finally
            {
                _rateLimiter.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            startedAt.Stop();
            WriteTrace(
                stageId: entry.Key.RelativeFilePath,
                batchId,
                requestId,
                requestItems: null,
                fingerprint: null,
                decisions,
                cacheHit: false,
                batchResult: null,
                startedAt.ElapsedMilliseconds,
                success: false,
                failure: ex,
                fallbackItemCount: 1,
                fallbackUnitKeys: new[] { unitKey },
                glossaryTerms: null,
                batchContext: entryBatch);

            _log($"[错误] 锁定术语自动修正失败（已保留原译文，转人工审核）: {ex.Message}");
            return new LockedTerminologyRepairResult
            {
                Attempted = true,
                Translation = null,
                RequestId = requestId,
                SkipReason = ex.Message,
            };
        }
    }

    /// 缓存结构校验（第4轮）：Cached ID 集合必须与请求 ID 集合完全一致。
    /// 检测 Missing / Extra / Duplicate，任一不满足都视为无效缓存。
    /// </summary>
    private static bool IsCacheUsable(
        CachedProviderBatchResponse cached,
        IReadOnlyList<DeepSeekTranslateRequestItem> requestItems)
    {
        if (cached.Items.Count != requestItems.Count || cached.Items.Count == 0)
        {
            return false;
        }

        var requestIds = new HashSet<string>(requestItems.Select(i => i.Id), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in cached.Items)
        {
            if (string.IsNullOrEmpty(item.Id) || !requestIds.Contains(item.Id))
            {
                return false;   // Extra / 空 id
            }
            if (!seen.Add(item.Id))
            {
                return false;   // Duplicate
            }
        }

        return seen.Count == requestIds.Count;   // Missing
    }

    /// <summary>把缓存响应重放成与网络响应同构的结果。</summary>
    private static DeepSeekBatchResult ReplayCached(CachedProviderBatchResponse cached)
        => new()
        {
            Items = cached.Items.ToDictionary(
                i => i.Id,
                i => new DeepSeekTranslateItem(i.Id, i.Translation, i.NeedsReview, i.Reason),
                StringComparer.Ordinal),
            ResponseId = cached.ResponseId,
            ResponseModel = cached.ResponseModel,
            PromptTokens = cached.PromptTokens,
            CompletionTokens = cached.CompletionTokens,
            TotalTokens = cached.TotalTokens,
            // 第8.5轮：旧缓存没有该字段 → null（向后兼容，不影响命中）
            ReasoningTokens = cached.ReasoningTokens,
            RetryCount = 0,
            DurationMs = 0,
        };

    /// <summary>第9.0B-P1轮：本批实际生效的源语言码（用于 Fingerprint v3）。</summary>
    private string ResolveEffectiveLanguageCode(IReadOnlyList<DiffEntry> batch)
    {
        if (!TranslationModePolicy.SendsKorean(_translationMode))
        {
            return SourceLanguageHelper.ToCode(SourceLanguage.English);
        }

        // KR_ONLY 或「选择语言缺失 → 已回退韩文」⇒ 生效语言为韩文
        var fallback = _translationMode == TranslationMode.KoreanOnly
                       || batch.Any(e => !string.IsNullOrWhiteSpace(e.CanonicalKoreanText)
                                         && string.Equals(e.NewSourceText, e.CanonicalKoreanText, StringComparison.Ordinal));

        return fallback
            ? SourceLanguageHelper.ToCode(SourceLanguage.Korean)
            : SourceLanguageHelper.ToCode(TranslationModePolicy.GetReferenceLanguage(_translationMode) ?? SourceLanguage.English);
    }

    /// <summary>写 Trace（Trace 失败不得影响翻译）。</summary>
    private void WriteTrace(
        string? stageId,
        string batchId,
        string requestId,
        IReadOnlyList<DeepSeekTranslateRequestItem>? requestItems,
        RequestFingerprint? fingerprint,
        ThinkingDecision thinking,
        bool cacheHit,
        DeepSeekBatchResult? batchResult,
        long elapsedMs,
        bool success,
        Exception? failure,
        int fallbackItemCount = 0,
        IReadOnlyList<string>? fallbackUnitKeys = null,
        string? glossaryTerms = null,
        IReadOnlyList<DiffEntry>? batchContext = null)
    {
        var trace = _cacheServices?.Trace;
        if (trace is null)
        {
            return;
        }

        try
        {
            trace.Write(new TranslationTraceEntry
            {
                RunId = _cacheServices?.Run?.RunId ?? "unknown",
                RequestId = requestId,
                StageId = stageId,
                BatchId = batchId,
                UnitKeys = requestItems?.Select(i => i.Id).ToArray() ?? fallbackUnitKeys ?? Array.Empty<string>(),
                ItemCount = requestItems?.Count ?? fallbackItemCount,
                Provider = ProviderName,
                Model = _options.Model,
                Fingerprint = fingerprint?.Value,
                PromptHash = fingerprint?.PromptHash,
                ContextHash = fingerprint?.ContextHash,
                GlossarySubsetHash = fingerprint?.GlossarySubsetHash,
                // 第8.87轮：命中术语摘要（诊断「术语表是否真的注入」）
                GlossaryTerms = string.IsNullOrWhiteSpace(glossaryTerms) ? null : glossaryTerms,
                // 第8.875轮：本次运行的术语快照 Hash（诊断用，不参与请求指纹）
                GlossarySnapshotHash = _glossarySnapshot?.SnapshotHash,
                CharacterStyleHash = fingerprint?.CharacterStyleHash,
                // 第8.75轮：Thinking 决策可见性（只记 bool + 稳定原因码，不记源文）
                ThinkingEnabled = thinking.Enabled,
                ThinkingPolicyReason = thinking.Reason,
                CacheHit = cacheHit,
                NetworkCalled = !cacheHit && success,
                RetryCount = batchResult?.RetryCount ?? 0,
                DurationMs = batchResult?.DurationMs > 0 ? batchResult.DurationMs : elapsedMs,
                ResponseId = batchResult?.ResponseId,
                ResponseModel = batchResult?.ResponseModel,
                InputTokens = batchResult?.PromptTokens,
                OutputTokens = batchResult?.CompletionTokens,
                TotalTokens = batchResult?.TotalTokens,
                // 第8.5轮：区分隐藏推理成本与模型真正可见的译文输出
                ReasoningTokens = batchResult?.ReasoningTokens,
                VisibleOutputTokens = batchResult?.VisibleOutputTokens,
                Success = success,
                // 第9.0B-P1轮：四模式语义（EN_ONLY 的 Canonical 相关字段必须为 null = N/A，不得写 false）
                // 第9.0B 最终轮：usedKoreanFallback 只在「参考译本缺失 → 回退韩文」时为 true
                //   （KR_ONLY 的韩文就是原文，不存在“回退”，必须为 false）。
                TranslationMode = TranslationModeCodes.ToCode(_translationMode),
                EffectiveSourceLanguage = batchContext is null ? null : ResolveEffectiveLanguageCode(batchContext),
                UsedKoreanFallback = batchContext is null
                    ? null
                    : TranslationModePolicy.AllowsKoreanFallback(_translationMode)
                      && batchContext.Any(e => !string.IsNullOrWhiteSpace(e.CanonicalKoreanText)
                                               && string.Equals(e.NewSourceText, e.CanonicalKoreanText, StringComparison.Ordinal)),
                CanonicalKoreanPresent = batchContext is null || !TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode)
                    ? null
                    : batchContext.Any(e => !string.IsNullOrWhiteSpace(e.CanonicalKoreanText)),
                CanonicalChanged = batchContext is null || !TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode)
                    ? null
                    : batchContext.Any(e => !string.Equals(
                        e.CanonicalKoreanText ?? string.Empty, e.OldCanonicalKoreanText ?? string.Empty, StringComparison.Ordinal)),
                EnglishChanged = batchContext is null || !TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode)
                    ? null
                    : batchContext.Any(e => e.EnglishReferenceChanged == true),
                JapaneseChanged = batchContext is null || !TranslationModePolicy.UsesCanonicalKoreanDiff(_translationMode)
                    ? null
                    : batchContext.Any(e => e.JapaneseReferenceChanged == true),
                ErrorSummary = failure is null
                    ? null
                    : TranslationTraceWriter.Sanitize($"{failure.GetType().Name}: {failure.Message}"),
            });
        }
        catch (Exception traceEx)
        {
            _log($"[调试] Trace写入失败: {TranslationTraceWriter.Sanitize(traceEx.Message)}");
        }
    }

    /// <summary>
    /// 扫描 Batch 内文本，构造术语提示词。
    ///
    /// 第8.87轮：同时输出命中术语摘要（<paramref name="matchedTerms"/>）用于 Trace 排错 ——
    /// 这是定位「术语表到底有没有生效」的关键诊断信息（只记术语与译名，不记源文）。
    /// </summary>
    private string BuildGlossaryPromptForBatch(IReadOnlyList<DiffEntry> batch, out string matchedTerms)
    {
        matchedTerms = string.Empty;

        // 第9.0B-P1轮：生产计划已按**模式的多源文本**匹配并注入 MatchedTerms ⇒
        // 直接复用（术语只匹配一次；Prompt / Validator / Trace 共用同一份）。
        var injected = batch
            .Where(e => e.MatchedTerms is { Count: > 0 })
            .SelectMany(e => e.MatchedTerms!)
            .GroupBy(term => term.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(term => term.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (injected.Count > 0)
        {
            var pairs = injected
                .Select(term => new KeyValuePair<string, GlossaryEntry>(
                    term.Source,
                    new GlossaryEntry { Translation = term.Target, Locked = term.Locked }))
                .ToList();
            matchedTerms = GlossaryService.DescribeSubset(pairs);
            return GlossaryService.BuildGlossaryPrompt(pairs);
        }

        // 第8.875轮：优先使用**运行时术语快照**（一次运行一个术语版本；Prompt 与 Validator 同源）
        var termCount = _glossarySnapshot?.Count ?? _glossary?.Count ?? 0;
        if (termCount == 0)
        {
            return string.Empty;
        }

        var texts = batch
            .SelectMany(e => new[] { e.NewSourceText, e.OldSourceText, e.OldTranslation })
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!);

        var subset = _glossarySnapshot is not null
            ? _glossarySnapshot.SelectTerms(texts)
            : _glossary!.SelectTerms(texts);
        if (subset.Count == 0)
        {
            return string.Empty;
        }

        // 稳定排序：Dictionary 插入顺序不同但语义相同时，提示词与请求指纹必须一致
        var ordered = subset
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        matchedTerms = GlossaryService.DescribeSubset(ordered);
        return GlossaryService.BuildGlossaryPrompt(ordered);
    }

    /// <summary>
    /// 第8.87轮：本批文本分类（仅当批内分类完全一致时返回，否则 null）。
    ///
    /// 分类指令必须与整批文本匹配，混合批不下发分类指令，避免给出错误要求。
    /// </summary>
    private static TextCategory? ResolveUniformCategory(IReadOnlyList<DiffEntry> batch)
    {
        if (batch.Count == 0)
        {
            return null;
        }

        var first = TextCategoryHelper.FromRelativePath(batch[0].Key.RelativeFilePath);
        for (var i = 1; i < batch.Count; i++)
        {
            if (TextCategoryHelper.FromRelativePath(batch[i].Key.RelativeFilePath) != first)
            {
                return null;
            }
        }

        return first;
    }


    /// <summary>
    /// 扫描 Batch 内 Speaker，构造角色风格提示词。
    /// </summary>
    private string BuildCharacterStylePromptForBatch(IReadOnlyList<DiffEntry> batch)
    {
        if (_characterStyles is null || _characterStyles.Count == 0)
        {
            return string.Empty;
        }

        var speakers = batch.Select(e => e.Speaker).Distinct();
        var styles = _characterStyles.SelectStyles(speakers);
        if (styles.Count == 0)
        {
            return string.Empty;
        }

        // 稳定排序：语义相同时提示词与请求指纹必须一致
        var ordered = styles
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return CharacterStyleService.BuildStylePrompt(ordered);
    }

    /// <summary>
    /// 从字符串还原 UnitKey。
    /// </summary>
    private static UnitKey ParseKey(string keyString)
    {
        var parts = keyString.Split('|');
        return new UnitKey
        {
            RelativeFilePath = parts.Length > 0 ? parts[0] : string.Empty,
            RecordId = parts.Length > 1 ? parts[1] : string.Empty,
            FieldPath = parts.Length > 2 ? parts[2] : string.Empty,
        };
    }

    /// <inheritdoc />
    public bool IsThinkingEnabled(DiffEntry entry) => _thinkingPolicy.Decide(entry).Enabled;

    /// <inheritdoc />
    public string ResolveThinkingReason(DiffEntry entry) => _thinkingPolicy.Decide(entry).Reason;

    /// <summary>
    /// 释放 Provider 自己创建的资源（第7轮）。
    ///
    /// Ownership 约定：
    ///   - <see cref="IDeepSeekBatchClient"/>（含其内部 HttpClient）：
    ///     由 Provider 自己 new 时才由 Provider 释放；外部注入（测试 Fake / 共享实例）由调用方负责，
    ///     绝不在此处误释放共享 HttpClient。
    ///   - <see cref="SemaphoreSlim"/> 限流器：Provider 创建，Provider 释放。
    ///
    /// 可重复调用（幂等且线程安全）：多次 Dispose 不会抛异常。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
        {
            return;
        }

        try
        {
            if (_ownsClient && _client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        finally
        {
            _rateLimiter.Dispose();
        }
    }
}
