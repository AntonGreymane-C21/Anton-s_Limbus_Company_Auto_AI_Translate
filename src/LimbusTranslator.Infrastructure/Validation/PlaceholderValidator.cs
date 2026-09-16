using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Placeholder;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 占位符校验（硬安全）。
///
/// 复用 <see cref="PlaceholderProtector.Validate"/>，不重新实现第二套 Placeholder Parser。
/// 校验对象是“已恢复的最终译文”：把译文里的原始占位符重新映射为源文的标记后交给 Validate。
/// </summary>
public sealed class PlaceholderValidator : ITranslationValidator
{
    private readonly PlaceholderProtector _protector = new();

    /// <inheritdoc />
    public string Name => nameof(PlaceholderValidator);

    /// <inheritdoc />
    public ValidationRuleKind Kind => ValidationRuleKind.HardSafety;

    /// <inheritdoc />
    public IReadOnlyList<ValidationIssue> Validate(ValidationContext context)
    {
        if (context is null)
        {
            return Array.Empty<ValidationIssue>();
        }

        var source = ValidationTextTools.Safe(context.SourceText);
        var translation = ValidationTextTools.Safe(context.Translation);

        // 空译文交给 EmptyTranslationValidator，避免重复噪声
        if (translation.Length == 0)
        {
            return Array.Empty<ValidationIssue>();
        }

        var issues = new List<ValidationIssue>();

        // 1) 未恢复的保护标记（AI 自造标记 / 恢复失败）
        var leftoverMarkers = PlaceholderProtector.MarkerRegex.Matches(translation).Count;
        if (leftoverMarkers > 0)
        {
            issues.Add(Error(context, $"译文中存在 {leftoverMarkers} 处未恢复的保护标记"));
        }

        if (source.Length == 0)
        {
            return issues;
        }

        var protectedSource = _protector.Protect(source);
        if (protectedSource.OriginalPlaceholders.Count == 0)
        {
            return issues;
        }

        // 2) 复用 PlaceholderProtector.Validate
        var protectedTranslation = translation;
        for (var i = 0; i < protectedSource.OriginalPlaceholders.Count; i++)
        {
            var original = protectedSource.OriginalPlaceholders[i];
            var marker = $"__LT_PH_{i + 1:D4}__";
            if (original.Length == 0 || original == marker)
            {
                continue;
            }
            protectedTranslation = protectedTranslation.Replace(original, marker, StringComparison.Ordinal);
        }

        var validation = _protector.Validate(protectedTranslation, protectedSource);

        // 方括号类（[NOTE] / [CharacterName]）在历史译文里经常被本地化（[介绍] / [注意]），
        // 它不是运行期格式占位符，按“宁可漏报”原则不作为占位符缺失/多余判定。
        // 真正的富文本标签丢失由 TagValidator 负责（Error）。
        var filteredMissing = validation.Missing.Where(m => !IsBracketMarker(m, protectedSource)).ToList();
        var filteredDuplicated = validation.Duplicated.Where(m => !IsBracketMarker(m, protectedSource)).ToList();
        var filteredUnknown = validation.Unknown.ToList();

        if (filteredMissing.Count > 0 || filteredDuplicated.Count > 0 || filteredUnknown.Count > 0)
        {
            var parts = new List<string>();
            if (filteredMissing.Count > 0)
            {
                parts.Add($"缺失 {string.Join(",", filteredMissing)}");
            }
            if (filteredDuplicated.Count > 0)
            {
                parts.Add($"重复 {string.Join(",", filteredDuplicated)}");
            }
            if (filteredUnknown.Count > 0)
            {
                parts.Add($"未知 {string.Join(",", filteredUnknown)}");
            }
            issues.Add(Error(context, "Placeholder 校验失败: " + string.Join("；", parts)));
        }

        // 3) 数量级校验：Validate 无法表达“占位符出现次数减少”（仍存在但不完整）
        var translatedOriginals = _protector.Protect(translation).OriginalPlaceholders;
        var sourceCounts = CountByValue(protectedSource.OriginalPlaceholders);
        var targetCounts = CountByValue(translatedOriginals);
        var alreadyReported = new HashSet<string>(validation.Missing, StringComparer.Ordinal);

        foreach (var pair in sourceCounts)
        {
            if (IsBracketLike(pair.Key))
            {
                continue;
            }

            targetCounts.TryGetValue(pair.Key, out var targetCount);
            if (targetCount >= pair.Value)
            {
                continue;
            }

            var marker = MarkerFor(protectedSource.OriginalPlaceholders, pair.Key);
            if (marker is not null && alreadyReported.Contains(marker))
            {
                continue;
            }

            issues.Add(Error(context, $"占位符 {Truncate(pair.Key)} 数量减少: 源文 {pair.Value} 次 → 译文 {targetCount} 次"));
        }

        foreach (var pair in targetCounts)
        {
            sourceCounts.TryGetValue(pair.Key, out var sourceCount);
            if (pair.Value <= sourceCount || IsBracketLike(pair.Key))
            {
                continue;
            }

            issues.Add(Error(context, $"译文出现源文不存在的占位符 {Truncate(pair.Key)}（{pair.Value} 次）"));
        }

        return issues;
    }

    private ValidationIssue Error(ValidationContext context, string message)
        => ValidationIssueFactory.Create(
            context,
            ValidationIssueCodes.PlaceholderMismatch,
            ValidationSeverity.Error,
            ValidationCategory.Placeholder,
            Name,
            message);

    private static Dictionary<string, int> CountByValue(IReadOnlyList<string> values)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            counts[value] = counts.TryGetValue(value, out var c) ? c + 1 : 1;
        }
        return counts;
    }

    private static string? MarkerFor(IReadOnlyList<string> originals, string value)
    {
        for (var i = 0; i < originals.Count; i++)
        {
            if (string.Equals(originals[i], value, StringComparison.Ordinal))
            {
                return $"__LT_PH_{i + 1:D4}__";
            }
        }
        return null;
    }

    private static bool IsBracketLike(string value)
        => value.Length >= 2 && value[0] == '[' && value[^1] == ']';

    /// <summary>标记 __LT_PH_NNNN__ 对应的原始占位符是否是方括号类。</summary>
    private static bool IsBracketMarker(string marker, PlaceholderProtectedText protectedSource)
    {
        var digits = marker.Replace("__LT_PH_", string.Empty).Replace("__", string.Empty);
        if (!int.TryParse(digits, out var index) || index < 1 || index > protectedSource.OriginalPlaceholders.Count)
        {
            return false;
        }

        return IsBracketLike(protectedSource.OriginalPlaceholders[index - 1]);
    }

    private static string Truncate(string value)
        => value.Length <= 24 ? value : value[..24] + "...";
}
