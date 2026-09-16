using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 数字校验（启发式，保守）。
/// 只比较可见的阿拉伯数字 token（1 / 20 / 3.5 / 100%），已排除占位符、标签属性与保护标记。
/// 不做 “one ↔ 一” 这类语言数字语义转换。
/// </summary>
public sealed class NumberValidator : ITranslationValidator
{
    /// <inheritdoc />
    public string Name => nameof(NumberValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.SourceText) || string.IsNullOrWhiteSpace(context.Translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        var sourceNumbers = ValidationTextTools.ExtractNumbers(context.SourceText);
        var targetNumbers = ValidationTextTools.ExtractNumbers(context.Translation);
        if (sourceNumbers.Count == 0 && targetNumbers.Count == 0)
        {
            return Array.Empty<ValidationIssue>();
        }

        var sourceSet = sourceNumbers.OrderBy(n => n, StringComparer.Ordinal).ToList();
        var targetSet = targetNumbers.OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (sourceSet.SequenceEqual(targetSet, StringComparer.Ordinal))
        {
            return Array.Empty<ValidationIssue>();
        }

        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.NumberMismatch,
                ValidationSeverity.Warning,
                ValidationCategory.Format,
                Name,
                $"可见数字不一致: 源文 [{string.Join(",", sourceSet)}] → 译文 [{string.Join(",", targetSet)}]"),
        };
    }
}

/// <summary>
/// 换行校验（启发式）。
/// 只比较“字符串内容里的换行”（真实换行 + 字面量 \n）数量，
/// 与文件 CRLF / LF 编码差异无关。
/// </summary>
public sealed class LineBreakValidator : ITranslationValidator
{
    /// <inheritdoc />
    public string Name => nameof(LineBreakValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.SourceText) || string.IsNullOrWhiteSpace(context.Translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        var source = ValidationTextTools.CountLineBreaks(context.SourceText);
        var target = ValidationTextTools.CountLineBreaks(context.Translation);
        if (source.Real == target.Real && source.Literal == target.Literal)
        {
            return Array.Empty<ValidationIssue>();
        }

        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.LineBreakMismatch,
                ValidationSeverity.Warning,
                ValidationCategory.Format,
                Name,
                $"换行数量不一致: 源文(换行 {source.Real} / 字面\\n {source.Literal}) → "
                + $"译文(换行 {target.Real} / 字面\\n {target.Literal})"),
        };
    }
}

/// <summary>
/// 长度校验（启发式，保守）。
/// 中英文天然长度不同：只在极端异常时告警，源文过短时直接跳过。
/// </summary>
public sealed class LengthValidator : ITranslationValidator
{
    private readonly ValidationOptions _options;

    public LengthValidator(ValidationOptions? options = null)
    {
        _options = options ?? ValidationOptions.Default;
    }

    /// <inheritdoc />
    public string Name => nameof(LengthValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null || string.IsNullOrWhiteSpace(context.SourceText) || string.IsNullOrWhiteSpace(context.Translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        var sourceLength = context.SourceText.Trim().Length;
        var targetLength = context.Translation.Trim().Length;
        if (sourceLength < _options.LengthMinSourceLength)
        {
            return Array.Empty<ValidationIssue>();
        }

        var ratio = (double)targetLength / sourceLength;
        if (ratio >= _options.LengthMinRatio && ratio <= _options.LengthMaxRatio)
        {
            return Array.Empty<ValidationIssue>();
        }

        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.LengthAnomaly,
                ValidationSeverity.Warning,
                ValidationCategory.Format,
                Name,
                $"长度比例异常: 源文 {sourceLength} → 译文 {targetLength}（比例 {ratio:0.00}，允许 "
                + $"{_options.LengthMinRatio:0.00}~{_options.LengthMaxRatio:0.00}）"),
        };
    }
}
