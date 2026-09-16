using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Placeholder;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>一条被违反的锁定术语（结构化对象；**禁止**从 Message 反向解析）。</summary>
public sealed record LockedTermViolation(string Source, string Target)
{
    public override string ToString() => $"{Source}→{Target}";
}

/// <summary>
/// 锁定术语判定（第9.0C.2轮）：**TerminologyValidator 与 Locked 术语自动修正共用的唯一实现**。
///
/// 语义（与修订前的 TerminologyValidator 完全一致，只是把判定结果结构化）：
///   - 只针对 <c>Locked = true</c> 且 <c>Target</c> 非空、且不要求保留英文的术语；
///   - 术语必须**按独立单词**命中源文（复用 <see cref="ValidationTextTools.ContainsAsWord"/>，
///     避免 Ring ⊂ During 这类误报）；
///   - 译文未包含规定译法 ⇒ 视为违规；
///   - <c>Preferred</c>（Locked = false）**永远不产生违规**（软约束，不触发自动修正）。
///
/// 术语来源：调用方传入的 <see cref="TerminologyRequirement"/>（生产链中来自
/// <c>DiffEntry.MatchedTerms</c>，即生产计划单次匹配的结果）——本类**不重新扫描术语表**。
/// </summary>
public static class LockedTerminologyCheck
{
    /// <summary>术语修正请求的稳定原因码（进入 Trace 与指纹）。</summary>
    public const string RepairReasonKind = "LockedTerminologyRepair";

    /// <summary>
    /// 找出译文未遵守的锁定术语。
    /// </summary>
    /// <param name="sourceText">源文（用于确认术语确实出现在源文中）</param>
    /// <param name="translation">当前译文</param>
    /// <param name="terms">候选术语（一般为 MatchedTerms；可为 null）</param>
    /// <param name="requireWordBoundary">是否要求源文按独立单词命中（默认与 Validator 选项一致）</param>
    public static IReadOnlyList<LockedTermViolation> FindViolations(
        string? sourceText,
        string? translation,
        IEnumerable<TerminologyRequirement>? terms,
        bool requireWordBoundary = true)
    {
        if (terms is null || string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(translation))
        {
            return Array.Empty<LockedTermViolation>();
        }

        var violations = new List<LockedTermViolation>();
        foreach (var term in terms)
        {
            if (!term.Locked || term.PreservedAsEnglish || string.IsNullOrWhiteSpace(term.Target))
            {
                continue;
            }

            if (requireWordBoundary && !ValidationTextTools.ContainsAsWord(sourceText, term.Source))
            {
                continue;
            }

            if (translation.Contains(term.Target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            violations.Add(new LockedTermViolation(term.Source, term.Target));
        }

        return violations;
    }
}
