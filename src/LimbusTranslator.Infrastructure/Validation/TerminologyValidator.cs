using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 术语校验（启发式）。
///
/// 只针对 locked=true 的术语：源文命中该术语时，译文必须包含规定译法，否则 Warning。
/// 术语候选来自 GlossaryService（Prompt 与校验共用同一匹配来源）；
/// 但校验额外要求“独立单词”命中，避免 Ring ⊂ During 这类子串误报（可用选项关闭）。
/// </summary>
public sealed class TerminologyValidator : ITranslationValidator
{
    private const int MaxListedTerms = 5;

    private readonly ValidationOptions _options;

    public TerminologyValidator(ValidationOptions? options = null)
    {
        _options = options ?? ValidationOptions.Default;
    }

    /// <inheritdoc />
    public string Name => nameof(TerminologyValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null || context.Terminology.Count == 0)
        {
            return Array.Empty<ValidationIssue>();
        }

        if (string.IsNullOrWhiteSpace(context.SourceText) || string.IsNullOrWhiteSpace(context.Translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        var mismatched = LockedTerminologyCheck
            .FindViolations(
                context.SourceText,
                context.Translation,
                context.Terminology,
                _options.TerminologyRequireWordBoundary)
            .Select(violation => violation.ToString())
            .ToList();

        if (mismatched.Count == 0)
        {
            return Array.Empty<ValidationIssue>();
        }

        var listed = string.Join(",", mismatched.Take(MaxListedTerms));
        var suffix = mismatched.Count > MaxListedTerms ? $" 等 {mismatched.Count} 项" : string.Empty;

        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.TerminologyMismatch,
                ValidationSeverity.Warning,
                ValidationCategory.Terminology,
                Name,
                $"译文未使用锁定术语译法: {listed}{suffix}"),
        };
    }
}
