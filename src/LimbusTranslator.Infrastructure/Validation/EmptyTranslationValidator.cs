using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 空译文校验（硬安全）。
///
/// 【第2轮规则】空源文本身不是错误：
///   Source 非空 + Translation 空            → Error
///   Source 空   + Translation 空            → 不报错
///   Source 空   + Translation 非空          → 不报错（保留既有游戏 / 旧汉化语义）
/// </summary>
public sealed class EmptyTranslationValidator : ITranslationValidator
{
    /// <inheritdoc />
    public string Name => nameof(EmptyTranslationValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.HardSafety;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null)
        {
            return Array.Empty<ValidationIssue>();
        }

        // 空源文不参与“空译文”判定
        if (!SourceTextGuard.IsReusable(context.SourceText))
        {
            return Array.Empty<ValidationIssue>();
        }

        if (string.IsNullOrWhiteSpace(context.Translation))
        {
            return new[]
            {
                ValidationIssueFactory.Create(
                    context,
                    ValidationIssueCodes.EmptyTranslation,
                    ValidationSeverity.Error,
                    ValidationCategory.Structure,
                    Name,
                    "源文非空但译文为空"),
            };
        }

        return Array.Empty<ValidationIssue>();
    }
}
