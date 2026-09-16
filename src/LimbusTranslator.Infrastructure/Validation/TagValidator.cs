using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 富文本标签结构校验（硬安全）。
///
/// 比较“标签名多重集”，因此：
///   &lt;color=#FFFFFF&gt; 与 &lt;color=#ffffff&gt; 属性差异不会误报；
///   标签丢失 / 变多 / 开闭不成对（数量变化）→ Error。
/// </summary>
public sealed class TagValidator : ITranslationValidator
{
    /// <inheritdoc />
    public string Name => nameof(TagValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.HardSafety;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null)
        {
            return Array.Empty<ValidationIssue>();
        }

        var sourceTags = ValidationTextTools.ExtractTagNames(context.SourceText);
        var targetTags = ValidationTextTools.ExtractTagNames(context.Translation);
        if (sourceTags.Count == 0 && targetTags.Count == 0)
        {
            return Array.Empty<ValidationIssue>();
        }

        var sourceCounts = CountByValue(sourceTags);
        var targetCounts = CountByValue(targetTags);

        var missing = new List<string>();
        var extra = new List<string>();
        foreach (var pair in sourceCounts)
        {
            targetCounts.TryGetValue(pair.Key, out var target);
            if (target < pair.Value)
            {
                missing.Add($"{pair.Key}×{pair.Value - target}");
            }
        }
        foreach (var pair in targetCounts)
        {
            sourceCounts.TryGetValue(pair.Key, out var source);
            if (source < pair.Value)
            {
                extra.Add($"{pair.Key}×{pair.Value - source}");
            }
        }

        if (missing.Count == 0 && extra.Count == 0)
        {
            return Array.Empty<ValidationIssue>();
        }

        var parts = new List<string>();
        if (missing.Count > 0)
        {
            parts.Add("缺失 " + string.Join(",", missing));
        }
        if (extra.Count > 0)
        {
            parts.Add("多余 " + string.Join(",", extra));
        }

        return new[]
        {
            ValidationIssueFactory.Create(
                context,
                ValidationIssueCodes.TagMismatch,
                ValidationSeverity.Error,
                ValidationCategory.Structure,
                Name,
                "富文本标签结构不一致: " + string.Join("；", parts)),
        };
    }

    private static Dictionary<string, int> CountByValue(IReadOnlyList<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            counts[value] = counts.TryGetValue(value, out var c) ? c + 1 : 1;
        }
        return counts;
    }
}
