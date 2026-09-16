using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 译文与源文相同校验（启发式，保守）。
///
/// 只针对“明显应该翻译的完整英文文本”，必须跳过：
///   空串 / 纯缩写（HP、SP、E.G.O、ID、UI）/ 纯数字 / 纯符号 /
///   Placeholder 与 Tag 主导的文本 / Glossary 明确要求保留英文的项。
/// </summary>
public sealed class SameAsSourceValidator : ITranslationValidator
{
    private readonly ValidationOptions _options;

    public SameAsSourceValidator(ValidationOptions? options = null)
    {
        _options = options ?? ValidationOptions.Default;
    }

    /// <inheritdoc />
    public string Name => nameof(SameAsSourceValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.Heuristic;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null)
        {
            return Array.Empty<ValidationIssue>();
        }

        var source = ValidationTextTools.Safe(context.SourceText);
        var translation = ValidationTextTools.Safe(context.Translation);

        // 空文本必须直接跳过（"" == "" 不是问题）
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translation))
        {
            return Array.Empty<ValidationIssue>();
        }

        var visibleSource = Normalize(ValidationTextTools.ToVisibleText(source));
        var visibleTranslation = Normalize(ValidationTextTools.ToVisibleText(translation));

        // 去掉占位符 / 标签后为空 → 不判定
        if (visibleSource.Length == 0 || visibleTranslation.Length == 0)
        {
            return Array.Empty<ValidationIssue>();
        }

        // 先排除 Glossary 明确要求保留英文的术语
        if (IsPreservedByGlossary(context, visibleSource))
        {
            return Array.Empty<ValidationIssue>();
        }

        if (!string.Equals(visibleSource, visibleTranslation, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<ValidationIssue>();
        }

        var words = ValidationTextTools.SplitLatinWords(visibleSource);
        var letters = visibleSource.Count(c => c < 128 && char.IsLetter(c));
        if (words.Count < _options.SameAsSourceMinWords || letters < _options.SameAsSourceMinLetters)
        {
            return Array.Empty<ValidationIssue>();
        }

        // 全部是合法缩写 / 允许词 → 跳过（如 "E.G.O" 或 "HP MAX"）
        if (words.All(w => context.AllowedEnglishTerms.Contains(w)))
        {
            return Array.Empty<ValidationIssue>();
        }

        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.SameAsSource,
                ValidationSeverity.Warning,
                ValidationCategory.Content,
                Name,
                $"译文与源文完全相同，疑似未翻译（{words.Count} 词 / {letters} 字母）"),
        };
    }

    private static string Normalize(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private static bool IsPreservedByGlossary(ValidationContext context, string visibleSource)
    {
        foreach (var term in context.Terminology)
        {
            if (term.PreservedAsEnglish
                && ValidationTextTools.ContainsAsWord(visibleSource, term.Source))
            {
                return true;
            }
        }

        // 源文本身就在允许英文词表里（HP / SP / E.G.O 等）
        return context.AllowedEnglishTerms.Contains(visibleSource);
    }
}
