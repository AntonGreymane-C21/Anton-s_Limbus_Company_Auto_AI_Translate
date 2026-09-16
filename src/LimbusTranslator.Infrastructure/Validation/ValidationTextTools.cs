using System.Text.RegularExpressions;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Placeholder;

namespace LimbusTranslator.Infrastructure.Validation;

/// <summary>
/// 校验用文本工具（共用一份实现，避免各 Validator 重复造正则）。
/// 只做只读字符串处理，不产生任何副作用。
/// </summary>
internal static class ValidationTextTools
{
    /// <summary>富文本标签（含属性与自闭合）</summary>
    private static readonly Regex TagRegex = new(
        @"</?\s*[A-Za-z][A-Za-z0-9_\-]*(?:\s[^<>]*)?/?>", RegexOptions.Compiled);

    /// <summary>标签名（忽略属性差异）</summary>
    private static readonly Regex TagNameRegex = new(
        @"</?\s*([A-Za-z][A-Za-z0-9_\-]*)", RegexOptions.Compiled);

    /// <summary>阿拉伯数字 token（含小数与百分号）</summary>
    private static readonly Regex NumberRegex = new(
        @"\d+(?:[.,]\d+)?%?", RegexOptions.Compiled);

    /// <summary>拉丁单词（含内部连字符 / 撇号）</summary>
    private static readonly Regex LatinWordRegex = new(
        @"[A-Za-z][A-Za-z'’\-]*", RegexOptions.Compiled);

    /// <summary>非空保护</summary>
    internal static string Safe(string? text) => text ?? string.Empty;

    /// <summary>去掉内部保护标记后的文本</summary>
    internal static string RemoveMarkers(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : PlaceholderProtector.MarkerRegex.Replace(text, " ");

    /// <summary>可见文本：去掉保护标记与富文本标签（数字 / 英文残留分析用）</summary>
    internal static string ToVisibleText(string? text)
    {
        var withoutMarkers = RemoveMarkers(text);
        return withoutMarkers.Length == 0 ? string.Empty : TagRegex.Replace(withoutMarkers, " ");
    }

    /// <summary>提取标签名（小写，忽略属性；重复出现即计入多次）</summary>
    internal static List<string> ExtractTagNames(string? text)
    {
        var result = new List<string>();
        var source = RemoveMarkers(text);
        if (source.Length == 0)
        {
            return result;
        }

        foreach (Match m in TagNameRegex.Matches(source))
        {
            result.Add(m.Groups[1].Value.ToLowerInvariant());
        }
        return result;
    }

    /// <summary>提取可见数字 token（已剔除占位符与标签属性）</summary>
    internal static List<string> ExtractNumbers(string? text)
    {
        var result = new List<string>();
        var visible = ToVisibleText(text);
        if (visible.Length == 0)
        {
            return result;
        }

        foreach (Match m in NumberRegex.Matches(visible))
        {
            result.Add(NormalizeNumber(m.Value));
        }
        return result;
    }

    /// <summary>归一化数字：去掉千分位逗号，保留小数点与百分号</summary>
    private static string NormalizeNumber(string token)
    {
        if (token.Length > 4 && token.Contains(','))
        {
            var beforeDot = token.Split('.')[0];
            if (Regex.IsMatch(beforeDot, @"^\d{1,3}(,\d{3})+%?$"))
            {
                return token.Replace(",", string.Empty);
            }
        }
        return token;
    }

    /// <summary>统计换行：真实换行数（CRLF 记 1）与字面量 \n 数量</summary>
    internal static (int Real, int Literal) CountLineBreaks(string? text)
    {
        var s = Safe(text);
        var real = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\r')
            {
                real++;
                if (i + 1 < s.Length && s[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (s[i] == '\n')
            {
                real++;
            }
        }

        var literal = 0;
        var idx = 0;
        while (true)
        {
            var found = s.IndexOf("\\n", idx, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }
            literal++;
            idx = found + 2;
        }

        return (real, literal);
    }

    /// <summary>拉丁字符（ASCII 字母数字）判定</summary>
    internal static bool IsLatinWordChar(char c) => c < 128 && char.IsLetterOrDigit(c);

    /// <summary>
    /// token 是否以“独立单词”形式出现在文本中（避免 Ring 命中 During）。
    ///
    /// 第8.875轮：统一委托 <see cref="Core.Text.TermMatcher"/>，
    /// 使 Prompt 侧与 Validator 侧的术语匹配使用同一套实现（英文词边界 / 韩文左侧边界）。
    /// </summary>
    internal static bool ContainsAsWord(string? text, string? token)
        => Core.Text.TermMatcher.ContainsTerm(text, token);

    /// <summary>拆分拉丁单词</summary>
    internal static List<string> SplitLatinWords(string? text)
    {
        var result = new List<string>();
        var s = Safe(text);
        if (s.Length == 0)
        {
            return result;
        }

        foreach (Match m in LatinWordRegex.Matches(s))
        {
            result.Add(m.Value);
        }
        return result;
    }

    /// <summary>是否包含韩文（委托 <see cref="SourceLanguageDetector"/>，保持单一实现）</summary>
    internal static bool ContainsHangul(string? text)
        => SourceLanguageDetector.ContainsHangul(Safe(text));

    /// <summary>单个字符是否为韩文（委托 <see cref="SourceLanguageDetector"/>）</summary>
    internal static bool IsHangul(char value) => SourceLanguageDetector.IsHangul(value);

    /// <summary>统计某个子串出现次数（区分大小写，用于占位符计数）</summary>
    internal static int CountOccurrences(string? text, string? token)
    {
        var s = Safe(text);
        if (s.Length == 0 || string.IsNullOrEmpty(token))
        {
            return 0;
        }

        var count = 0;
        var idx = 0;
        while (true)
        {
            var found = s.IndexOf(token, idx, StringComparison.Ordinal);
            if (found < 0)
            {
                return count;
            }
            count++;
            idx = found + token.Length;
        }
    }

    /// <summary>
    /// 连续拉丁词串（英文残留检测用）。
    /// 规则：被中文字符打断则断开；允许词按“中性”处理（既不计数也不断开）；
    /// 只有连续非允许词达到 minWords 且总字母数达到 minChars 才算残留。
    /// </summary>
    internal static IReadOnlyList<string> FindLatinRuns(
        string? text,
        int minWords,
        int minChars,
        IReadOnlySet<string>? allowedTerms)
    {
        var runs = new List<string>();
        var visible = ToVisibleText(text);
        if (visible.Length == 0)
        {
            return runs;
        }

        var matches = LatinWordRegex.Matches(visible);
        var current = new List<string>();
        var currentChars = 0;
        var lastEnd = -1;

        void Flush()
        {
            if (current.Count >= minWords && currentChars >= minChars)
            {
                runs.Add(string.Join(" ", current));
            }
            current = new List<string>();
            currentChars = 0;
        }

        foreach (Match m in matches)
        {
            if (lastEnd >= 0)
            {
                var gap = visible[lastEnd..m.Index];
                // 中文（或其他非拉丁可见字符）会打断英文残留串
                if (gap.Any(IsCjk))
                {
                    Flush();
                }
            }

            var word = m.Value;
            if (allowedTerms is not null && allowedTerms.Contains(word))
            {
                // 允许词：中性，不断开也不计数
                lastEnd = m.Index + m.Length;
                continue;
            }

            current.Add(word);
            currentChars += word.Length;
            lastEnd = m.Index + m.Length;
        }

        Flush();
        return runs;
    }

    /// <summary>CJK 字符判定（用于判断英文串是否被中文打断）</summary>
    internal static bool IsCjk(char c) =>
        (c >= 0x3000 && c <= 0x303F)   // CJK 符号
        || (c >= 0x3400 && c <= 0x4DBF) // 扩展 A
        || (c >= 0x4E00 && c <= 0x9FFF) // 基本区
        || (c >= 0xF900 && c <= 0xFAFF) // 兼容表意
        || (c >= 0xFF00 && c <= 0xFFEF); // 全角
}
