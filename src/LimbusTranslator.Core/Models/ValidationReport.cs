namespace LimbusTranslator.Core.Models;

/// <summary>
/// 单个翻译单元的校验报告。
///
/// 【重要】Issues 与 NeedsReview 决策是两层概念：
///   Issues 只描述“发现了什么”，是否需要人工审核由
///   <see cref="ValidationNeedsReviewPolicy"/> 按来源策略决定。
/// 禁止写成 issues.Count &gt; 0 =&gt; NeedsReview = true。
/// </summary>
public sealed class ValidationReport
{
    /// <summary>复合主键</summary>
    public required UnitKey Key { get; init; }

    /// <summary>本次采用的校验策略（由译文来源解析）</summary>
    public required ValidationPolicy Policy { get; init; }

    /// <summary>结构化问题列表（按 Severity 降序、Code 升序排列）</summary>
    public required IReadOnlyList<ValidationIssue> Issues { get; init; }

    /// <summary>Error 数量</summary>
    public int ErrorCount => Issues.Count(i => i.Severity == ValidationSeverity.Error);

    /// <summary>Warning 数量</summary>
    public int WarningCount => Issues.Count(i => i.Severity == ValidationSeverity.Warning);

    /// <summary>Info 数量</summary>
    public int InfoCount => Issues.Count(i => i.Severity == ValidationSeverity.Info);

    /// <summary>是否存在 Error（硬安全问题）</summary>
    public bool HasError => ErrorCount > 0;

    /// <summary>是否存在 Warning（启发式问题）</summary>
    public bool HasWarning => WarningCount > 0;

    /// <summary>参与本次校验的校验器名称</summary>
    public IReadOnlyList<string> Validators { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 面向 UI / ReviewReason 的可读汇总；无问题时为 null。
    /// 结构化 Issues 永远保留，本字符串只是展示层摘要。
    /// </summary>
    public string? Summary =>
        Issues.Count == 0
            ? null
            : string.Join("；", Issues.Select(i => $"{i.Code}: {i.Message}"));
}
