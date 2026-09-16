namespace LimbusTranslator.Core.Models;

/// <summary>
/// 校验问题。
///
/// 【第2轮】新增稳定机器码 <see cref="Code"/> 与 <see cref="Category"/>：
/// 第3轮 ReleaseGate 与后续 UI 需要按 Code / Severity / Category 稳定处理，
/// 不允许只依赖自然语言 Message。
/// </summary>
public sealed class ValidationIssue
{
    /// <summary>复合主键</summary>
    public required UnitKey Key { get; init; }

    /// <summary>稳定机器码（见 <see cref="ValidationIssueCodes"/>）</summary>
    public required string Code { get; init; }

    /// <summary>严重级别</summary>
    public ValidationSeverity Severity { get; init; } = ValidationSeverity.Error;

    /// <summary>问题类别</summary>
    public ValidationCategory Category { get; init; } = ValidationCategory.Content;

    /// <summary>校验器名称（如 PlaceholderValidator）</summary>
    public required string Validator { get; init; }

    /// <summary>问题描述（面向人阅读，禁止在逻辑中依赖它的具体措辞）</summary>
    public required string Message { get; init; }

    public override string ToString() => $"{Code}({Severity}): {Message}";
}

/// <summary>
/// 校验问题类别（供 ReleaseGate / UI 分组）。
/// </summary>
public enum ValidationCategory
{
    /// <summary>结构性安全（空译文、标签、占位符等硬问题）</summary>
    Structure,

    /// <summary>占位符</summary>
    Placeholder,

    /// <summary>格式（数字、换行、长度等）</summary>
    Format,

    /// <summary>语言残留（英文 / 韩文）</summary>
    Language,

    /// <summary>术语一致性</summary>
    Terminology,

    /// <summary>内容质量（与源文相同等）</summary>
    Content,
}

/// <summary>
/// 校验严重级别。
/// </summary>
public enum ValidationSeverity
{
    /// <summary>信息</summary>
    Info,

    /// <summary>警告（启发式，不阻止发布）</summary>
    Warning,

    /// <summary>错误（硬安全问题，禁止写入最终输出 / 需要人工确认）</summary>
    Error,
}
