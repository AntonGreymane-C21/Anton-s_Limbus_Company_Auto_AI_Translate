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

    public string Describe()
    {
        var parts = new List<string>();
        if (Missing.Count > 0) parts.Add($"缺失 {string.Join(",", Missing)}");
        if (Duplicated.Count > 0) parts.Add($"重复 {string.Join(",", Duplicated)}");
        if (Unknown.Count > 0) parts.Add($"未知 {string.Join(",", Unknown)}");
        return parts.Count == 0 ? "通过" : string.Join("；", parts);
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
            throw new InvalidOperationException($"[错误] Placeholder 校验失败: {validation.Describe()}");
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
    /// 宽容恢复：校验失败时不抛异常，尽量恢复标记；
    /// 对确实缺失的标记，在文本末尾补回对应占位符（避免 AI 损坏占位符导致数据缺失）。
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

        // 缺失的标记：把原始占位符追加到末尾，保证关键数据不丢
        if (!validation.IsValid)
        {
            foreach (var missing in validation.Missing)
            {
                if (int.TryParse(missing.Replace("__LT_PH_", string.Empty).Replace("__", string.Empty), out var idx)
                    && idx >= 1 && idx <= protectedSource.OriginalPlaceholders.Count)
                {
                    result += protectedSource.OriginalPlaceholders[idx - 1];
                }
            }
        }

        return (result, validation);
    }

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
