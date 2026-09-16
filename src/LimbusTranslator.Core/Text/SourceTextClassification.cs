namespace LimbusTranslator.Core.Text;

/// <summary>
/// 源文本分类（第8.5轮）。
/// </summary>
public enum SourceTextKind
{
    /// <summary>null / 空 / 纯空白（由第7轮 Empty fast-path 处理）。</summary>
    Empty,

    /// <summary>
    /// 纯符号 / 标点：没有任何 Unicode 字母或数字，也没有结构标记（占位符 / 标签字符）。
    /// 这类文本没有可翻译的自然语言内容，直接原样保留，不消耗 API。
    /// </summary>
    SymbolOnly,

    /// <summary>其它（含自然语言，或含占位符 / 标签语义的内容）→ 走正常翻译流程。</summary>
    NaturalLanguage,
}

/// <summary>
/// 源文本分类的唯一实现（第8.5轮）。
///
/// 【为什么必须集中在一处】Symbol-only 判定若在各层各写一套正则，
/// 迟早出现"某层判 SymbolOnly、另一层判需要翻译"的不一致。
///
/// 【保守规则】只有**完全没有** Unicode 字母与数字、且不含结构标记字符的文本才判定为 SymbolOnly：
///   - `???`、`...`、`……`、`—`、`---`、`!!!`、`?!`、`→`、`★`  → SymbolOnly
///   - `{0}`、`%s`、`#{var}`、`[TETH]`、`<br>`、`<color=#fff>`、`HP`、`123`、`E.G.O` → 不判定为 SymbolOnly
///     （前两组含字母/数字；结构标记字符 `{}&lt;&gt;%[]\` 进一步兜底，避免吞掉占位符与标签语义）。
/// </summary>
public static class SourceTextClassification
{
    /// <summary>
    /// 结构标记字符：出现任一即认为文本带有 Placeholder / Tag / 转义语义，禁止 Passthrough。
    /// </summary>
    private const string StructuralMarkerCharacters = "{}<>%[]\\";

    /// <summary>
    /// 分类源文本。
    /// </summary>
    public static SourceTextKind Classify(string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return SourceTextKind.Empty;
        }

        var hasVisibleContent = false;
        foreach (var ch in sourceText)
        {
            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            hasVisibleContent = true;

            // 含字母或数字 → 可能承载语义（含 HP / 123 / E.G.O / %s / {0} / [TETH] / <br>）
            if (char.IsLetterOrDigit(ch))
            {
                return SourceTextKind.NaturalLanguage;
            }

            // 含结构标记字符 → 交给 Placeholder / Tag 流程处理
            if (StructuralMarkerCharacters.IndexOf(ch) >= 0)
            {
                return SourceTextKind.NaturalLanguage;
            }
        }

        return hasVisibleContent ? SourceTextKind.SymbolOnly : SourceTextKind.Empty;
    }

    /// <summary>是否可直接原样保留（纯符号 / 标点）。</summary>
    public static bool IsSymbolOnly(string? sourceText) => Classify(sourceText) == SourceTextKind.SymbolOnly;
}
