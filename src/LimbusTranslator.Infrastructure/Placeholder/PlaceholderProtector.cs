using System.Text;
using System.Text.RegularExpressions;

namespace LimbusTranslator.Infrastructure.Placeholder;

/// <summary>
/// Placeholder 保护结果：保护文本 + 原始占位符映射表。
/// </summary>
public sealed class PlaceholderProtectedText
{
    /// <summary>被保护后的文本（占位符被替换为 __LT_PH_0001__ 标记）</summary>
    public required string ProtectedText { get; init; }

    /// <summary>标记 → 原始占位符 的映射（按编号顺序）</summary>
    public required IReadOnlyList<string> OriginalPlaceholders { get; init; }
}

/// <summary>
/// Placeholder 校验结果。
/// </summary>
public sealed class PlaceholderValidation
{
    public bool IsValid { get; init; }

    /// <summary>缺失的标记（如 __LT_PH_0001__）</summary>
    public required IReadOnlyList<string> Missing { get; init; }

    /// <summary>重复的标记</summary>
    public required IReadOnlyList<string> Duplicated { get; init; }

    /// <summary>未知的标记（AI 自己造的）</summary>
    public required IReadOnlyList<string> Unknown { get; init; }

    /// <summary>
    /// 人类可读描述。
    ///
    /// 第9.0C.14轮：传入 <paramref name="source"/> 时，把内部标记 <c>__LT_PH_0001__</c> 映射回**真实占位符**
    /// （例如 <c>&lt;i&gt;×1、&lt;/i&gt;×1、{0}</c>）—— 旧实现直接把内部标记名写给用户看，
    /// 报告里出现「缺失 __LT_PH_0001__,__LT_PH_0002__」对人类毫无意义。
    /// <paramref name="source"/> 为 null 时退化为标记名（保持旧行为，供没有映射表的调用点使用）。
    /// </summary>
    public string Describe(PlaceholderProtectedText? source = null)
    {
        var parts = new List<string>();
        if (Missing.Count > 0) parts.Add($"缺失 {DescribeMarkers(Missing, source)}");
        if (Duplicated.Count > 0) parts.Add($"重复 {DescribeMarkers(Duplicated, source)}");
        if (Unknown.Count > 0) parts.Add($"未知 {DescribeMarkers(Unknown, source)}");
        return parts.Count == 0 ? "通过" : string.Join("；", parts);
    }

    /// <summary>
    /// 标记列表 → 真实占位符列表（同一占位符出现多次时写作 <c>&lt;i&gt;×2</c>）。
    /// </summary>
    public static string DescribeMarkers(IReadOnlyList<string> markers, PlaceholderProtectedText? source)
    {
        if (markers.Count == 0)
        {
            return string.Empty;
        }

        if (source is null)
        {
            return string.Join(",", markers);
        }

        var names = markers
            .Select(marker => MarkerToOriginal(marker, source) ?? marker)
            .ToList();
        var grouped = names
            .GroupBy(name => name, StringComparer.Ordinal)
            .Select(group => group.Count() > 1 ? $"{group.Key}×{group.Count()}" : group.Key);
        return string.Join("、", grouped);
    }

    /// <summary>标记 <c>__LT_PH_NNNN__</c> → 原始占位符（越界或格式不符返回 null）。</summary>
    public static string? MarkerToOriginal(string marker, PlaceholderProtectedText source)
    {
        if (source is null || string.IsNullOrEmpty(marker))
        {
            return null;
        }

        var digits = marker.Replace("__LT_PH_", string.Empty).Replace("__", string.Empty);
        return int.TryParse(digits, out var index) && index >= 1 && index <= source.OriginalPlaceholders.Count
            ? source.OriginalPlaceholders[index - 1]
            : null;
    }
}

/// <summary>
/// Placeholder 保护器。
///
/// 高优先级功能（文档 §29/§30）。
/// 在 AI 翻译前，将文本中的占位符（{0}、%s、\n、富文本标签、[名字] 等）
/// 替换为 __LT_PH_NNNN__ 标记，避免 AI 损坏它们；翻译后恢复并严格校验。
/// </summary>
public sealed class PlaceholderProtector
{
    /// <summary>保护标记正则</summary>
    public static readonly Regex MarkerRegex = new(
        @"__LT_PH_(\d{4})__", RegexOptions.Compiled);

    /// <summary>需要保护的占位符正则（按优先级组合）</summary>
    private static readonly Regex PlaceholderRegex = new(
        @"\{[0-9]+(:[^}]*)?\}" +              // {0} / {1:0.0} 等格式化占位符
        @"|%[sd]|%[0-9]+[sd]" +               // %s %d %1s
        @"|\\n|\\t|\\r" +                     // 字面 \n \t \r（源码字符串）
        @"|<color=[^>]+>|</color>" +          // <color=#FFFFFF> </color>
        @"|<size=[^>]+>|</size>" +            // <size=95%> </size>
        @"|<b>|</b>|<i>|</i>|<br\s*/>" +      // 加粗/斜体/换行标签
        @"|\[[^\]]{1,32}\]" +                 // [CharacterName] 类
        @"|#\{[^}]+\}",                       // #{var}
        RegexOptions.Compiled);

    private readonly int _maxPlaceholdersPerText;

    public PlaceholderProtector(int maxPlaceholdersPerText = 500)
    {
        _maxPlaceholdersPerText = maxPlaceholdersPerText;
    }

    /// <summary>
    /// 保护文本：提取占位符并替换为标记。
    /// </summary>
    public PlaceholderProtectedText Protect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new PlaceholderProtectedText { ProtectedText = text, OriginalPlaceholders = Array.Empty<string>() };
        }

        var originals = new List<string>();
        var sb = new StringBuilder(text.Length + 32);
        var pos = 0;
        var count = 0;

        foreach (Match m in PlaceholderRegex.Matches(text))
        {
            sb.Append(text, pos, m.Index - pos);
            var marker = $"__LT_PH_{count + 1:D4}__";
            sb.Append(marker);
            originals.Add(m.Value);
            pos = m.Index + m.Length;
            count++;
        }
        sb.Append(text, pos, text.Length - pos);

        return new PlaceholderProtectedText
        {
            ProtectedText = sb.ToString(),
            OriginalPlaceholders = originals,
        };
    }

    /// <summary>
    /// 恢复：将翻译结果中的标记替换回原始占位符。
    /// 必须先通过 Validate，否则抛出异常。
    /// </summary>
    public string Restore(string translatedText, PlaceholderProtectedText protectedSource)
    {
        var validation = Validate(translatedText, protectedSource);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException($"[错误] Placeholder 校验失败: {validation.Describe(protectedSource)}");
        }

        var result = translatedText;
        for (var i = 0; i < protectedSource.OriginalPlaceholders.Count; i++)
        {
            var marker = $"__LT_PH_{i + 1:D4}__";
            result = result.Replace(marker, protectedSource.OriginalPlaceholders[i]);
        }
        return result;
    }

    /// <summary>
    /// 宽容恢复：校验失败时不抛异常，尽量恢复标记。
    ///
    /// 第9.0C.14轮修复：旧实现在标记缺失时**一律把原始占位符追加到句尾** ——
    /// 模型漏掉 <c>&lt;i&gt;</c> 时会在中文句末多出 <c>&lt;i&gt;</c>，漏掉 <c>{0}</c> 时数字占位符会被扔到句尾
    ///（游戏里显示位置直接错位）。现在按类型就位：
    ///   - **起始标签**（<c>&lt;i&gt;</c> / <c>&lt;color=…&gt;</c>）⇒ 补到**最前面**；
    ///   - **结束标签**（<c>&lt;/i&gt;</c> / <c>&lt;/color&gt;</c>）⇒ 补到**最后面**；
    ///   - 其它（<c>{0}</c> / <c>%s</c> / <c>\n</c> / 自闭合标签）⇒ 仍然补到末尾（无法推断语义位置，至少不丢数据）。
    /// 无论哪种情况，只要发生过缺失，调用方仍会把该条目标为待人工审核。
    /// </summary>
    /// <returns>(恢复后的文本, 校验结果)</returns>
    public (string Text, PlaceholderValidation Validation) RestoreLenient(
        string translatedText, PlaceholderProtectedText protectedSource)
    {
        var validation = Validate(translatedText, protectedSource);
        var result = translatedText;

        // 先替换存在的标记
        for (var i = 0; i < protectedSource.OriginalPlaceholders.Count; i++)
        {
            var marker = $"__LT_PH_{i + 1:D4}__";
            result = result.Replace(marker, protectedSource.OriginalPlaceholders[i]);
        }

        if (!validation.IsValid)
        {
            var prefix = new StringBuilder();
            var suffix = new StringBuilder();
            foreach (var missing in validation.Missing)
            {
                var original = PlaceholderValidation.MarkerToOriginal(missing, protectedSource);
                if (string.IsNullOrEmpty(original))
                {
                    continue;
                }

                if (IsOpeningTag(original))
                {
                    prefix.Append(original);
                }
                else
                {
                    suffix.Append(original);
                }
            }

            if (prefix.Length > 0)
            {
                result = prefix + result;
            }

            if (suffix.Length > 0)
            {
                result += suffix;
            }
        }

        return (result, validation);
    }

    /// <summary>是否为起始标签（<c>&lt;i&gt;</c>、<c>&lt;color=…&gt;</c>）；自闭合 <c>&lt;br/&gt;</c> 不算。</summary>
    private static bool IsOpeningTag(string value)
        => value.Length >= 3
           && value[0] == '<'
           && value[1] != '/'
           && !value.EndsWith("/>", StringComparison.Ordinal)
           && char.IsLetter(value[1]);

    /// <summary>
    /// 严格校验（文档 §30）：数量一致、编号一致、不缺失、不重复、无未知。
    /// </summary>
    public PlaceholderValidation Validate(string translatedText, PlaceholderProtectedText protectedSource)
    {
        var expectedCount = protectedSource.OriginalPlaceholders.Count;
        var expectedMarkers = new HashSet<string>();
        for (var i = 0; i < expectedCount; i++)
        {
            expectedMarkers.Add($"__LT_PH_{i + 1:D4}__");
        }

        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in MarkerRegex.Matches(translatedText))
        {
            found[m.Value] = found.TryGetValue(m.Value, out var c) ? c + 1 : 1;
        }

        var missing = expectedMarkers.Where(m => !found.ContainsKey(m)).ToList();
        var duplicated = found.Where(kv => kv.Value > 1 && expectedMarkers.Contains(kv.Key))
            .Select(kv => kv.Key).ToList();
        var unknown = found.Keys.Where(m => !expectedMarkers.Contains(m)).ToList();

        return new PlaceholderValidation
        {
            IsValid = missing.Count == 0 && duplicated.Count == 0 && unknown.Count == 0,
            Missing = missing,
            Duplicated = duplicated,
            Unknown = unknown,
        };
    }
}
