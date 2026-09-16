using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Translation;

/// <summary>
/// Agent 执行结果。
/// </summary>
public sealed class AgentExecutionResult
{
    /// <summary>Stage（文件）标识</summary>
    public required string StageId { get; init; }

    /// <summary>是否成功</summary>
    public bool IsSuccess { get; init; }

    /// <summary>成功翻译条目数</summary>
    public int TranslatedCount { get; init; }

    /// <summary>需要审核条目数</summary>
    public int NeedsReviewCount { get; init; }

    /// <summary>Exact TM（UnitKey + SourceHash）命中条目数（第8.8轮 GUI 统计）</summary>
    public int TmHitCount { get; init; }

    /// <summary>纯符号直通条目数（TranslationSource.Passthrough，不调用 Provider）</summary>
    public int PassthroughCount { get; init; }

    /// <summary>空源文跳过条目数（不调用 Provider）</summary>
    public int EmptySourceSkippedCount { get; init; }

    /// <summary>空源文继承旧中文条目数（不调用 Provider）</summary>
    public int EmptySourceInheritedCount { get; init; }

    /// <summary>校验 Error 数（HardSafety + 启发式合计，GUI 只做展示）</summary>
    public int ValidationErrorCount { get; init; }

    /// <summary>校验 Warning 数</summary>
    public int ValidationWarningCount { get; init; }

    /// <summary>第9.0C.2轮：锁定术语自动修正尝试次数</summary>
    public int TerminologyRepairAttemptCount { get; init; }

    /// <summary>第9.0C.2轮：锁定术语自动修正成功（修正后不再违规）次数</summary>
    public int TerminologyRepairSuccessCount { get; init; }

    /// <summary>错误信息（失败时）</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Coordinator 汇总结果。
/// </summary>
public sealed class CoordinatorResult
{
    public required IReadOnlyList<AgentExecutionResult> Agents { get; init; }

    /// <summary>成功 Agent 数</summary>
    public int SuccessCount => Agents.Count(a => a.IsSuccess);

    /// <summary>失败 Agent 数</summary>
    public int FailedCount => Agents.Count(a => !a.IsSuccess);

    /// <summary>总翻译条目数</summary>
    public int TotalTranslated => Agents.Sum(a => a.TranslatedCount);

    /// <summary>Exact TM 命中总数（第8.8轮 GUI 统计）</summary>
    public int TotalTmHits => Agents.Sum(a => a.TmHitCount);

    /// <summary>纯符号直通总数</summary>
    public int TotalPassthrough => Agents.Sum(a => a.PassthroughCount);

    /// <summary>空源文跳过总数</summary>
    public int TotalEmptySourceSkipped => Agents.Sum(a => a.EmptySourceSkippedCount);

    /// <summary>空源文继承总数</summary>
    public int TotalEmptySourceInherited => Agents.Sum(a => a.EmptySourceInheritedCount);

    /// <summary>需要审核总数</summary>
    public int TotalNeedsReview => Agents.Sum(a => a.NeedsReviewCount);

    /// <summary>校验 Error 总数</summary>
    public int TotalValidationErrors => Agents.Sum(a => a.ValidationErrorCount);

    /// <summary>校验 Warning 总数</summary>
    public int TotalValidationWarnings => Agents.Sum(a => a.ValidationWarningCount);

    /// <summary>第9.0C.2轮：锁定术语自动修正尝试总数</summary>
    public int TotalTerminologyRepairAttempts => Agents.Sum(a => a.TerminologyRepairAttemptCount);

    /// <summary>第9.0C.2轮：锁定术语自动修正成功总数</summary>
    public int TotalTerminologyRepairSuccesses => Agents.Sum(a => a.TerminologyRepairSuccessCount);
}
