using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Infrastructure.Translation;

/// <summary>
/// 锁定术语自动修正服务（第9.0C.2轮）—— 把「Locked 术语」从「告警」升级为「可执行约束」。
///
/// 触发条件（全部满足才发起修正，最多 **1 次**）：
///   1. 首轮校验报告里存在 <c>TERMINOLOGY_MISMATCH</c>（按结构化 Code 判定，**不解析 Message**）；
///   2. 该违规由 **Locked** 术语引起（Preferred 不触发）——判定来自 <c>DiffEntry.MatchedTerms</c>，
///      **不重新扫描术语表**（Longest Match Wins 的结果保持不变）；
///   3. Provider 支持修正（实现 <see cref="ILockedTerminologyRepairProvider"/>）。
///
/// 修正后必须重新进入**完整** ValidationPipeline（占位符 / 数字 / 标签 / 换行 / 残留 / 长度…），
/// 因为术语修正可能破坏结构：
///   - 重新校验无 **Error** ⇒ 采用修正译文；
///   - 重新校验出现 **Error** ⇒ 判定修正不可用，**回滚为原译文**（并保留原有术语告警 + 追加一条说明），
///     绝不把「术语修好了但占位符丢了」的结果写进 TM。
///
/// 本服务**只做修正**，不做自由翻译；也不修改四模式语义（请求仍由 Provider 按当前模式构造）。
/// </summary>
public sealed class LockedTerminologyRepairService
{
    /// <summary>本轮自动修正次数上限（硬限制，禁止无限重试烧 API）。</summary>
    public const int MaxAttempts = 1;

    private readonly ITranslationProvider _provider;
    private readonly ValidationPipeline _validation;
    private readonly Action<string>? _log;

    public LockedTerminologyRepairService(
        ITranslationProvider provider,
        ValidationPipeline? validation = null,
        Action<string>? log = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _validation = validation ?? new ValidationPipeline();
        _log = log;
    }

    /// <summary>
    /// 依据首轮校验报告决定是否执行一次锁定术语修正。
    /// 成功时直接把最终译文与最终校验结论写回 <paramref name="entry"/>。
    /// </summary>
    /// <param name="entry">目标条目（其 <c>MatchedTerms</c> 为生产计划注入的术语，可能为 null）</param>
    /// <param name="firstReport">首轮校验报告</param>
    /// <param name="preexistingIssues">首轮校验时传入的结构化前置问题（Provider 侧发现的问题）</param>
    /// <param name="providerRequestedReview">
    /// 模型自报 needs_review（第9.0C.2轮）：为 true 时**永不**因为术语修好而清除待审核标记（§十六）。
    /// </param>
    /// <param name="cancellationToken">取消令牌</param>
    public async Task<LockedTerminologyRepairOutcome> TryRepairAsync(
        DiffEntry entry,
        ValidationReport firstReport,
        IReadOnlyList<ValidationIssue>? preexistingIssues = null,
        bool providerRequestedReview = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // ① 结构化触发：存在 TERMINOLOGY_MISMATCH
        if (firstReport is null
            || !firstReport.Issues.Any(issue => issue.Code == ValidationIssueCodes.TerminologyMismatch))
        {
            return new LockedTerminologyRepairOutcome { Attempted = false, Note = "首轮校验无术语问题" };
        }

        // ② 只有 Locked 违规才修正（Preferred 属软约束，保持现状）
        if (entry.MatchedTerms is null)
        {
            return new LockedTerminologyRepairOutcome { Attempted = false, Note = "未注入术语（MatchedTerms 为空）" };
        }

        var violations = LockedTerminologyCheck.FindViolations(
            entry.NewSourceText,
            entry.Translation,
            entry.MatchedTerms);
        if (violations.Count == 0)
        {
            return new LockedTerminologyRepairOutcome { Attempted = false, Note = "仅 Preferred 术语或未命中锁定术语" };
        }

        // ③ Provider 必须支持修正
        if (_provider is not ILockedTerminologyRepairProvider repairProvider)
        {
            return new LockedTerminologyRepairOutcome { Attempted = false, Note = "Provider 不支持锁定术语修正" };
        }

        var lockedTerms = entry.MatchedTerms.Where(term => term.Locked).ToList();
        _log?.Invoke(
            $"[调试] 锁定术语校验：命中 {entry.MatchedTerms.Count} 条，违规 {violations.Count} 条"
            + $"（{string.Join("、", violations.Take(5))}）");

        var originalTranslation = entry.Translation ?? string.Empty;
        var result = await repairProvider
            .RepairLockedTerminologyAsync(entry, lockedTerms, originalTranslation, cancellationToken)
            .ConfigureAwait(false);

        return ApplyRepairResult(entry, violations, originalTranslation, preexistingIssues, providerRequestedReview, result);
    }

    /// <summary>把一次修正响应落成最终状态（采用 / 回滚）。</summary>
    private LockedTerminologyRepairOutcome ApplyRepairResult(
        DiffEntry entry,
        IReadOnlyList<LockedTermViolation> violations,
        string originalTranslation,
        IReadOnlyList<ValidationIssue>? preexistingIssues,
        bool providerRequestedReview,
        LockedTerminologyRepairResult result)
    {
        if (!result.Attempted)
        {
            return new LockedTerminologyRepairOutcome
            {
                Attempted = false,
                Note = result.SkipReason ?? "修正请求未发起",
            };
        }

        if (string.IsNullOrWhiteSpace(result.Translation))
        {
            _log?.Invoke($"[调试] 锁定术语自动修正：失败（{result.SkipReason ?? "返回为空"}），保留原译文并转人工审核");
            return new LockedTerminologyRepairOutcome
            {
                Attempted = true,
                AttemptCount = MaxAttempts,
                Succeeded = false,
                Violations = violations,
                RemainingViolations = violations,
                RequestId = result.RequestId,
                CacheHit = result.CacheHit,
                Note = result.SkipReason ?? "修正响应为空",
            };
        }

        // ④ 应用修正译文 → 重新进入**完整**校验链
        entry.Translation = result.Translation;
        var repairedReport = _validation.ValidateAndApply(entry, result.Issues);

        if (repairedReport.Issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            // 修正把结构改坏了（占位符 / 标签 / 空译…）⇒ 回滚为原译文，术语违规继续保留待人工确认
            entry.Translation = originalTranslation;
            var restoredReport = _validation.ValidateAndApply(entry, preexistingIssues);
            entry.ValidationIssues = restoredReport.Issues
                .Append(new ValidationIssue
                {
                    Key = entry.Key,
                    Code = ValidationIssueCodes.TerminologyMismatch,
                    Severity = ValidationSeverity.Warning,
                    Category = ValidationCategory.Terminology,
                    Validator = nameof(LockedTerminologyRepairService),
                    Message = "锁定术语自动修正被拒绝（修正结果引入结构问题），已保留原译文",
                })
                .ToList();
            entry.NeedsReview = true;
            entry.TerminologyRepairAttempts = MaxAttempts;
            entry.TerminologyRepairSucceeded = false;
            entry.TerminologyRepairNote = "修正结果触发结构错误，已回滚";

            _log?.Invoke("[调试] 锁定术语自动修正：修正结果触发结构错误，已回滚原译文并转人工审核");
            return new LockedTerminologyRepairOutcome
            {
                Attempted = true,
                AttemptCount = MaxAttempts,
                Succeeded = false,
                Violations = violations,
                RemainingViolations = violations,
                RequestId = result.RequestId,
                CacheHit = result.CacheHit,
                Note = "修正结果触发结构错误，已回滚",
            };
        }

        // ⑤ 重新确认术语是否真的不再违规
        var remaining = LockedTerminologyCheck.FindViolations(
            entry.NewSourceText, entry.Translation, entry.MatchedTerms);
        entry.TerminologyRepairAttempts = MaxAttempts;
        entry.TerminologyRepairSucceeded = remaining.Count == 0;
        entry.TerminologyRepairNote = remaining.Count == 0
            ? "已自动修正锁定术语"
            : $"仍有 {remaining.Count} 条锁定术语未遵守";

        if (remaining.Count > 0)
        {
            entry.NeedsReview = true;
            _log?.Invoke($"[调试] 锁定术语自动修正：仍有违规 {remaining.Count} 条，转人工审核");
        }
        else
        {
            // 术语已修好：只有在「没有其它 Warning / Error」且「模型没有自报 needs_review」时，
            // 才解除待审核标记 —— 既不留下早已解决的术语问题（§十四），也不清除其它 Review 原因（§十五、§十六）。
            var hasOtherIssues = repairedReport.Issues.Any(issue =>
                issue.Severity is ValidationSeverity.Warning or ValidationSeverity.Error);

            if (!hasOtherIssues && !providerRequestedReview)
            {
                entry.NeedsReview = false;
                entry.ReviewReason = null;
            }

            _log?.Invoke("[调试] 锁定术语自动修正：成功，剩余违规 0");
        }

        return new LockedTerminologyRepairOutcome
        {
            Attempted = true,
            AttemptCount = MaxAttempts,
            Succeeded = remaining.Count == 0,
            Translation = entry.Translation,
            Violations = violations,
            RemainingViolations = remaining,
            RequestId = result.RequestId,
            CacheHit = result.CacheHit,
            Note = remaining.Count == 0 ? "修正成功" : $"仍有 {remaining.Count} 条违规",
        };
    }
}
