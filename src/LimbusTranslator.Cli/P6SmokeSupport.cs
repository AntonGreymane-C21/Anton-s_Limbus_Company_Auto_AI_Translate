using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.DeepSeek;

namespace LimbusTranslator.Cli;

/// <summary>
/// 真实 API 预算被突破（第9.0B-P6轮）：任何一次「超出 8 单元 / 8 请求」都必须立即终止 Smoke。
/// </summary>
internal sealed class SmokeBudgetExceededException : Exception
{
    public SmokeBudgetExceededException(string message) : base(message)
    {
    }
}

/// <summary>
/// 真实 API 硬上限计数器（第9.0B-P6轮）。
///
/// 规则（代码层强制，不依赖“理论上只有 8 条”）：
///   - 真实翻译单元总数 &lt;= <see cref="MaxTranslationUnits"/>；
///   - 真实翻译网络请求总数 &lt;= <see cref="MaxNetworkRequests"/>；
///   - 任何一次即将越界的调用都会**先抛异常、后发送**（绝不发出第 9 条 / 第 9 次）。
/// </summary>
internal sealed class SmokeBudget
{
    public const int MaxTranslationUnits = 8;
    public const int MaxNetworkRequests = 8;

    private readonly object _gate = new();

    /// <summary>已预约（即将发送）的真实翻译单元数</summary>
    public int Units { get; private set; }

    /// <summary>已发送 / 即将发送的真实翻译请求数</summary>
    public int Requests { get; private set; }

    /// <summary>观测到的最大客户端重试次数（Smoke 关闭重试时恒为 0）</summary>
    public int ObservedRetryCount { get; private set; }

    /// <summary>预约一批单元（越界即抛，不会发出请求）。</summary>
    public void ReserveUnits(int count, string context)
    {
        lock (_gate)
        {
            if (count <= 0)
            {
                return;
            }

            if (Units + count > MaxTranslationUnits)
            {
                throw new SmokeBudgetExceededException(
                    $"[错误] 真实翻译单元上限保护触发：已用 {Units} + 本次 {count} > {MaxTranslationUnits}（{context}）。已立即终止。");
            }

            Units += count;
        }
    }

    /// <summary>预约一次真实翻译请求（越界即抛，不会发出请求）。</summary>
    public void ReserveRequest(string context)
    {
        lock (_gate)
        {
            if (Requests + 1 > MaxNetworkRequests)
            {
                throw new SmokeBudgetExceededException(
                    $"[错误] 真实网络请求上限保护触发：已用 {Requests} + 1 > {MaxNetworkRequests}（{context}）。已立即终止。");
            }

            Requests++;
        }
    }

    /// <summary>记录客户端返回的重试次数（重试也计入真实请求上限）。</summary>
    public void NoteRetry(int retryCount)
    {
        lock (_gate)
        {
            if (retryCount > ObservedRetryCount)
            {
                ObservedRetryCount = retryCount;
            }
        }
    }

    public string Describe() =>
        $"真实 API 预算：单元 {Units}/{MaxTranslationUnits}｜请求 {Requests}/{MaxNetworkRequests}｜重试 {ObservedRetryCount}";
}

/// <summary>
/// 带硬上限计数的批量客户端（第9.0B-P6轮）。
///
/// 位于 <c>DeepSeekTranslationProvider</c> 与真实 <c>DeepSeekClient</c> 之间：
///   1. 发送前先预约单元与请求（越界抛异常，绝不发出）；
///   2. 转发给真实客户端；
///   3. 记录返回的 <c>RetryCount</c>（Smoke 强制 MaxRetry = 0 ⇒ 1 次逻辑调用 = 1 次 HTTP 尝试）。
/// 不打印、不记录任何 Authorization / API Key。
/// </summary>
internal sealed class SmokeCappedBatchClient : IDeepSeekBatchClient
{
    private readonly IDeepSeekBatchClient _inner;
    private readonly SmokeBudget _budget;
    private readonly Action<string> _log;
    private readonly List<DeepSeekTranslateRequestItem> _sentItems = new();
    private readonly object _gate = new();

    public SmokeCappedBatchClient(IDeepSeekBatchClient inner, SmokeBudget budget, Action<string> log)
    {
        _inner = inner;
        _budget = budget;
        _log = log;
    }

    /// <summary>
    /// 真实发送给 DeepSeek 的请求项（SafeRequestSemanticSnapshot 的原始来源）。
    /// 只包含 Mode/Source/旧源/旧译文/韩文/旧韩文，**不含任何凭据**。
    /// </summary>
    public IReadOnlyList<DeepSeekTranslateRequestItem> SentItems
    {
        get
        {
            lock (_gate)
            {
                return _sentItems.ToArray();
            }
        }
    }

    public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
        string batchId,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        CancellationToken cancellationToken,
        string glossaryPrompt,
        string characterStylePrompt)
        => SendAsync(
            "legacy",
            items,
            () => _inner.TranslateBatchWithMetadataAsync(
                batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt));

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
        => SendAsync(
            TranslationModeCodes.ToCode(translationMode),
            items,
            () => _inner.TranslateBatchWithMetadataAsync(
                batchId, items, cancellationToken, glossaryPrompt, characterStylePrompt,
                thinking, includeModifiedRule, category, translationMode));

    private async Task<DeepSeekBatchResult> SendAsync(
        string context,
        IReadOnlyList<DeepSeekTranslateRequestItem> items,
        Func<Task<DeepSeekBatchResult>> send)
    {
        // 先预约（越界即抛，绝不发出第 9 条 / 第 9 次）
        _budget.ReserveUnits(items.Count, context);
        _budget.ReserveRequest(context);
        lock (_gate)
        {
            _sentItems.AddRange(items);
        }

        _log($"[调试] 真实 API 请求（模式 {context}）：{items.Count} 条｜{_budget.Describe()}");

        var result = await send();
        _budget.NoteRetry(result.RetryCount);
        return result;
    }
}

/// <summary>单条 Smoke 单元的期望（真实返回必须满足；违反即 Smoke 失败）。</summary>
internal sealed record SmokeUnitExpectation(
    string UnitKey,
    TranslationMode Mode,
    TranslationAction ExpectedAction,
    string ExpectedSelectedText,
    bool ExpectCanonicalKorean,
    bool ExpectOldCanonicalKorean,
    bool ExpectSalt,
    bool ExpectPlaceholder = false,
    string? ExpectedOldCanonicalKoreanText = null,
    string? ExpectedCanonicalKoreanText = null)
{
    /// <summary>占位符（必须被保留）——仅在 ExpectPlaceholder 时有意义。</summary>
    public const string Placeholder = "{0}";
}

/// <summary>单条 Smoke 单元的观测快照（SafeRequestSemanticSnapshot；不含任何凭据）。</summary>
internal sealed class SmokeUnitSnapshot
{
    public required string UnitKey { get; init; }
    public required string StageId { get; init; }
    public required string Mode { get; init; }
    public required string Action { get; init; }
    public string? SelectedSourceText { get; init; }
    public string? CanonicalKoreanText { get; init; }
    public string? OldCanonicalKoreanText { get; init; }
    public string? PreviousTargetText { get; init; }
    public string? SourceHashSalt { get; init; }
    public string? Fingerprint { get; init; }
    public bool HasGlossarySnapshot { get; init; }
    public bool CanonicalKoreanSent { get; init; }
    public bool OldCanonicalKoreanSent { get; init; }
    public bool PlaceholderProtectedInRequest { get; init; }
    public bool CacheHit { get; init; }
    public bool NetworkCalled { get; init; }
    public string? Translation { get; init; }
    public bool TranslationNonEmpty { get; init; }
    public string TmMatchType { get; init; } = "None";
    public int ValidationErrorCount { get; init; }
    public int ValidationWarningCount { get; init; }
    public bool NeedsReview { get; init; }
    public string? Provenance { get; init; }
    public bool PlaceholderPreserved { get; init; }

    // ── 第9.0B 最终真实四模式 Smoke：四模式对比所需的观测字段（全部来自真实链路） ──

    /// <summary>Trace 中的翻译模式码（en_only / kr_en / kr_jp / kr_only）</summary>
    public string? TraceTranslationMode { get; init; }

    /// <summary>本条目生效源语言（en / ko / ja）</summary>
    public string? EffectiveSourceLanguage { get; init; }

    /// <summary>是否回退韩文（EN_ONLY 为 null = N/A）</summary>
    public bool? UsedKoreanFallback { get; init; }

    /// <summary>是否携带韩文 Canonical（EN_ONLY 为 null = N/A）</summary>
    public bool? CanonicalKoreanPresent { get; init; }

    /// <summary>韩文 Canonical 是否变化（EN_ONLY 为 null = N/A）</summary>
    public bool? CanonicalChanged { get; init; }

    /// <summary>英文参考译本是否变化（EN_ONLY 为 null = N/A）</summary>
    public bool? EnglishChanged { get; init; }

    /// <summary>日文参考译本是否变化（EN_ONLY 为 null = N/A）</summary>
    public bool? JapaneseChanged { get; init; }

    /// <summary>真实请求最终是否开启 Thinking</summary>
    public bool? ThinkingEnabled { get; init; }

    /// <summary>Thinking 决策原因（稳定机器码）</summary>
    public string? ThinkingPolicyReason { get; init; }

    /// <summary>本批次输入 Token（真实 API 返回）</summary>
    public int? InputTokens { get; init; }

    /// <summary>本批次输出 Token</summary>
    public int? OutputTokens { get; init; }

    /// <summary>本批次推理 Token（Thinking 产生）</summary>
    public int? ReasoningTokens { get; init; }

    /// <summary>本批次总 Token</summary>
    public int? TotalTokens { get; init; }

    /// <summary>术语匹配结果（生产计划单次匹配后注入的那一份）</summary>
    public IReadOnlyList<string> MatchedTerms { get; init; } = Array.Empty<string>();

    /// <summary>真实请求中携带的上一句邻接参考（无 → null）</summary>
    public string? NeighborPrevious { get; init; }

    /// <summary>真实请求中携带的下一句邻接参考（无 → null）</summary>
    public string? NeighborNext { get; init; }

    /// <summary>Validator 问题码（含 Warning，用于观察模式级误报）</summary>
    public IReadOnlyList<string> ValidationIssueCodes { get; init; } = Array.Empty<string>();

    /// <summary>人工审核原因（AI 自报 needs_review 的原因 / 管线标记的原因；无 → null）</summary>
    public string? ReviewReason { get; init; }
}

/// <summary>一次 Smoke 阶段（一个翻译模式 + 一个运行阶段）的观测结果。</summary>
internal sealed class SmokeStageResult
{
    public required string StageName { get; init; }
    public required string TranslationMode { get; init; }
    public int AgentEntryCount { get; init; }
    public int ProviderCallCount { get; init; }
    public int UnitDelta { get; init; }
    public int RequestDelta { get; init; }
    public int BudgetUnits { get; init; }
    public int BudgetRequests { get; init; }
    public int TmHitCount { get; init; }
    public int TraceLineCount { get; init; }
    public int TraceNetworkCalledCount { get; init; }
    public int TraceCacheHitCount { get; init; }
    public int MergeWrittenEntryCount { get; init; }
    public int MergeIssueCount { get; init; }
    public bool MergeComplete { get; init; }
    public int GateMissingExpectedKeyCount { get; init; }
    public int GateUnexpectedOutputKeyCount { get; init; }
    public int GateErrorCount { get; init; }
    public int GateBlockingErrorCount { get; init; }
    public string GateStatus { get; init; } = string.Empty;
    public required string OutputDirectory { get; init; }
    public required IReadOnlyList<SmokeUnitSnapshot> Units { get; init; }
    public required IReadOnlyDictionary<string, string> Translations { get; init; }
    public required IReadOnlyList<string> Violations { get; init; }

    /// <summary>第9.0B 最终 Smoke：本阶段是否为 dry-run（只做计划预检，未发送任何请求）。</summary>
    public bool DryRun { get; init; }
}

/// <summary>Smoke 结果 JSON 落盘（只写 TEMP 目录）。</summary>
internal static class SmokeReportWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Save<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }
}
