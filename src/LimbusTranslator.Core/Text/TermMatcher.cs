using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LimbusTranslator.Core.Text;

/// <summary>
/// 术语匹配器（第8.875轮）：**Prompt 与 Validator 共用的唯一术语匹配实现**。
///
/// 背景：旧实现使用 `text.Contains(term, OrdinalIgnoreCase)` 做子串匹配，
/// 会出现 <c>Ring</c> 命中 <c>During</c> / <c>Spring</c> 之类的误报，
/// 而 Validator 侧使用的是“独立单词”判定 —— 两侧不一致会让「Prompt 认为命中、校验认为未命中」。
///
/// 规则：
///   - 英文型术语（字母/数字/空格/连字符/撇号/句点，且含至少一个字母）：
///     使用 ASCII 词边界（前后不得是字母或数字），因此
///       `Ring` 命中 "Ring attacks." / "The Ring." / "Ring's effect"
///       不命中 "During" / "Spring" / "Ringing"
///       `E.G.O` / `Don Quixote` / `Move-in Reg.` 等带标点、带空格的术语同样按整体 + 两端边界判定
///   - 韩文型术语（含 Hangul）：Hangul 不使用空格分词，因此采用
///     “**左侧边界**”判定（前面不能是另一个 Hangul 字母），允许后面跟随助词（如 싱클레어가）
///   - 其它（含中文）：保持保守的子串匹配（本轮重点为英文 / 韩文）
/// </summary>
public static partial class TermMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> PatternCache = new(StringComparer.Ordinal);

    private static readonly RegexOptions MatchOptions =
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>
    /// 文本中是否包含该术语（按术语类型选择边界规则）。
    /// </summary>
    public static bool ContainsTerm(string? text, string? term)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(term))
        {
            return false;
        }

        var value = term.Trim();
        if (value.Length == 0)
        {
            return false;
        }

        return Classify(value) switch
        {
            TermKind.English => ContainsEnglishTerm(text, value),
            TermKind.Hangul => ContainsHangulTerm(text, value),
            _ => text.Contains(value, StringComparison.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// 从一组文本中筛出命中的术语（返回命中的术语列表，保持传入术语的原始顺序）。
    /// </summary>
    public static IReadOnlyList<string> FindMatches(IEnumerable<string>? texts, IEnumerable<string>? terms)
    {
        var termList = terms?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? new List<string>();
        if (termList.Count == 0 || texts is null)
        {
            return Array.Empty<string>();
        }

        var hits = new List<string>();
        foreach (var term in termList)
        {
            foreach (var text in texts)
            {
                if (ContainsTerm(text, term))
                {
                    hits.Add(term);
                    break;
                }
            }
        }

        return hits;
    }

    private static bool ContainsEnglishTerm(string text, string term)
    {
        var pattern = PatternCache.GetOrAdd(term, BuildEnglishPattern);
        return pattern.IsMatch(text);
    }

    /// <summary>
    /// 英文术语的**受控词形后缀**（第9.0C.4轮）。
    ///
    /// 背景（真实缺陷）：术语库写 <c>Nursefather</c>，原文写 <c>Nursefathers</c>（英文复数），
    /// 旧规则 <c>(?&lt;![A-Za-z0-9])term(?![A-Za-z0-9])</c> 会因"后面还有字母"而**完全不命中**：
    ///   → MatchedTerms 为空 → Prompt 没有该 Locked 术语 → Validator 也看不到它 → 自动修正自然不触发。
    ///
    /// 本轮**不做通用 stemming**（游戏里大量自造词 / 角色名 / 组织名，通用词干算法极易误匹配），
    /// 只放开四种可证明安全的英语词形变化：复数 s / 保守的 es / 所有格 's / 复数所有格 s'。
    ///
    /// 安全性来自**右边界依旧禁止字母数字**：
    ///   <c>Mark</c> 命中 <c>Mark</c> / <c>Marks</c> / <c>Mark's</c> / <c>Marks'</c>，
    ///   但 <c>Marked</c>（ed）/ <c>Marker</c>（er）/ <c>Market</c>（et）/ <c>Landmark</c>（左边界是字母）全部不命中。
    /// </summary>
    private const string EnglishInflectionSuffix = "(?:s|es|'s|s')?";

    private static Regex BuildEnglishPattern(string term)
        => new(
            $"(?<![A-Za-z0-9]){Regex.Escape(term)}{EnglishInflectionSuffix}(?![A-Za-z0-9])",
            MatchOptions | RegexOptions.Compiled);

    private static bool ContainsHangulTerm(string text, string term)
    {
        var index = 0;
        while (index <= text.Length - term.Length)
        {
            var found = text.IndexOf(term, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            // 左侧必须是词首（前面不能是 Hangul 字母），避免嵌入更长的韩文串
            var leftOk = found == 0 || !IsHangul(text[found - 1]);
            if (leftOk)
            {
                return true;
            }

            index = found + 1;
        }

        return false;
    }
}

/// <summary>
/// 术语命中结果（含位置信息，用于重叠消解）。
/// </summary>
/// <param name="Term">术语原文</param>
/// <param name="Translation">术语译名</param>
/// <param name="Start">在源文本中的起始位置</param>
/// <param name="Length">匹配长度</param>
/// <param name="Locked">是否为强制术语</param>
public sealed record TermMatch(string Term, string Translation, int Start, int Length, bool Locked)
{
    /// <summary>
    /// 第9.0C.4轮（诊断用）：文本里**实际命中的表面形式**。
    ///
    /// 例：术语为 <c>Nursefather</c>、原文为 <c>Nursefathers</c> ⇒ 本属性 = <c>Nursefathers</c>。
    /// 不参与任何业务判定、不进入请求指纹 / 缓存 / 序列化，只用于排查"术语到底命中了哪个词形"。
    /// </summary>
    public string? MatchedSurface { get; init; }
}

/// <summary>
/// 术语匹配器（第8.875轮：词边界；第8.88轮：重叠消解 Longest Match Wins）。
/// Prompt 注入与 TerminologyValidator 使用**同一套匹配与选择实现**。
/// </summary>
public static partial class TermMatcher
{
    /// <summary>
    /// 找出文本中所有术语候选命中（含位置）。
    /// </summary>
    /// <param name="text">源文本</param>
    /// <param name="terms">(术语, 译名, 是否锁定) 列表</param>
    public static IReadOnlyList<TermMatch> FindAll(
        string? text,
        IEnumerable<(string Term, string Translation, bool Locked)> terms)
    {
        var result = new List<TermMatch>();
        if (string.IsNullOrEmpty(text) || terms is null)
        {
            return result;
        }

        foreach (var (term, translation, locked) in terms)
        {
            if (string.IsNullOrWhiteSpace(term))
            {
                continue;
            }

            var value = term.Trim();
            var kind = Classify(value);

            if (kind == TermKind.English)
            {
                // 英文：词边界 + 受控词形（复数 / 所有格）；可能多处出现，全部收集（位置用于重叠消解）
                var regex = PatternCache.GetOrAdd(value, BuildEnglishPattern);
                foreach (System.Text.RegularExpressions.Match m in regex.Matches(text))
                {
                    result.Add(new TermMatch(value, translation, m.Index, m.Length, locked)
                    {
                        MatchedSurface = m.Value,
                    });
                }
            }
            else if (kind == TermKind.Hangul)
            {
                var index = 0;
                while (index <= text.Length - value.Length)
                {
                    var found = text.IndexOf(value, index, StringComparison.Ordinal);
                    if (found < 0)
                    {
                        break;
                    }

                    if (found == 0 || !IsHangul(text[found - 1]))
                    {
                        result.Add(new TermMatch(value, translation, found, value.Length, locked));
                    }

                    index = found + 1;
                }
            }
            else
            {
                var index = 0;
                while (index <= text.Length - value.Length)
                {
                    var found = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase);
                    if (found < 0)
                    {
                        break;
                    }

                    result.Add(new TermMatch(value, translation, found, value.Length, locked));
                    index = found + 1;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 重叠消解（Longest Match Wins）：
    ///   - 按起点升序；
    ///   - 同一重叠区域：**更长者优先**（更具体的术语胜出）；
    ///   - 长度相同时：Locked 优先；
    ///   - 仍相同：术语 Key 的 Ordinal 升序（保证确定性）。
    ///
    /// 注意：**不做全局删除**——非重叠位置的短术语（例如另一段文本中的 `Power Up`）仍然保留。
    /// </summary>
    public static IReadOnlyList<TermMatch> SelectNonOverlapping(IEnumerable<TermMatch> matches)
    {
        var ordered = matches
            .OrderBy(m => m.Start)
            .ThenByDescending(m => m.Length)
            .ThenByDescending(m => m.Locked)
            .ThenBy(m => m.Term, StringComparer.Ordinal)
            .ToList();

        var selected = new List<TermMatch>();
        var occupiedUntil = -1;

        foreach (var match in ordered)
        {
            if (match.Start < occupiedUntil)
            {
                continue; // 与已选中的更长术语重叠 → 被遮蔽
            }

            selected.Add(match);
            occupiedUntil = match.Start + match.Length;
        }

        return selected;
    }

    /// <summary>词边界 + 重叠消解一步到位（返回最终有效术语）。</summary>
    public static IReadOnlyList<TermMatch> FindEffectiveMatches(
        string? text,
        IEnumerable<(string Term, string Translation, bool Locked)> terms)
        => SelectNonOverlapping(FindAll(text, terms));

    private static TermKind Classify(string term)
    {
        var hasHangul = false;
        var hasAsciiLetter = false;
        var onlyEnglishShape = true;

        foreach (var ch in term)
        {
            if (IsHangul(ch))
            {
                hasHangul = true;
                onlyEnglishShape = false;
                continue;
            }

            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
            {
                hasAsciiLetter = true;
                continue;
            }

            if (ch is >= '0' and <= '9' || ch == ' ' || ch == '-' || ch == '\'' || ch == '.')
            {
                continue;
            }

            onlyEnglishShape = false;
        }

        if (onlyEnglishShape && hasAsciiLetter)
        {
            return TermKind.English;
        }

        return hasHangul ? TermKind.Hangul : TermKind.Other;
    }

    private static bool IsHangul(char ch)
        => (ch >= '\uAC00' && ch <= '\uD7A3')
           || (ch >= '\u1100' && ch <= '\u11FF')
           || (ch >= '\u3130' && ch <= '\u318F');

    private enum TermKind
    {
        English,
        Hangul,
        Other,
    }
}