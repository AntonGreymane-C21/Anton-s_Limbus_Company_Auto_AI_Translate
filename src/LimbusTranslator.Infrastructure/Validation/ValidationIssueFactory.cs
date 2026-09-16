using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 校验问题构造器（统一 Code / Severity / Category / Validator 的填充方式）。
/// </summary>
internal static class ValidationIssueFactory
{
    internal static ValidationIssue Create(
        ValidationContext context,
        string code,
        ValidationSeverity severity,
        ValidationCategory category,
        string validator,
        string message)
        => new()
        {
            Key = context.Key,
            Code = code,
            Severity = severity,
            Category = category,
            Validator = validator,
            Message = message,
        };
}
