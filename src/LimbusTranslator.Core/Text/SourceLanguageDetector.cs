namespace LimbusTranslator.Core.Text;

/// <summary>
/// 源语言检测（第8.75轮）。
///
/// 【唯一实现】Hangul（韩文）Unicode 范围判断只在此处定义，供：
///   - Validator 侧 <c>SourceLanguageAnomalyValidator</c> / <c>KoreanResidueValidator</c>
///   - Thinking 策略侧 <c>TranslationThinkingPolicy</c>
/// 共同使用，禁止复制多套 Unicode 范围判断。
///
/// 语义：
///   - 只判断**字符是否属于韩文范围**，不承担字段过滤（model 等内部韩文 ID 由 Parser 黑名单排除）。
/// </summary>
public static class SourceLanguageDetector
{
    /// <summary>Hangul 音节</summary>
    public const char HangulSyllablesStart = '\uAC00';
    public const char HangulSyllablesEnd = '\uD7A3';

    /// <summary>Hangul Jamo</summary>
    public const char HangulJamoStart = '\u1100';
    public const char HangulJamoEnd = '\u11FF';

    /// <summary>Hangul 兼容字母</summary>
    public const char HangulCompatibilityJamoStart = '\u3130';
    public const char HangulCompatibilityJamoEnd = '\u318F';

    /// <summary>该字符是否为韩文。</summary>
    public static bool IsHangul(char value)
        => value is (>= HangulSyllablesStart and <= HangulSyllablesEnd)
            or (>= HangulJamoStart and <= HangulJamoEnd)
            or (>= HangulCompatibilityJamoStart and <= HangulCompatibilityJamoEnd);

    /// <summary>文本中是否包含韩文。</summary>
    public static bool ContainsHangul(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (IsHangul(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>统计韩文字符数量（用于日志与 Issue 消息，不输出文本内容）。</summary>
    public static int CountHangul(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if (IsHangul(ch))
            {
                count++;
            }
        }

        return count;
    }

    // ───────────────────────── 日文假名（第9.0B-P1轮） ─────────────────────────

    /// <summary>平假名</summary>
    public const char HiraganaStart = '\u3040';
    public const char HiraganaEnd = '\u309F';

    /// <summary>片假名（含片假名音标扩展）</summary>
    public const char KatakanaStart = '\u30A0';
    public const char KatakanaEnd = '\u30FF';

    /// <summary>半角片假名</summary>
    public const char HalfwidthKatakanaStart = '\uFF66';
    public const char HalfwidthKatakanaEnd = '\uFF9D';

    /// <summary>
    /// 该字符是否为日文假名（平假名 / 片假名 / 半角片假名）。
    /// **只判假名，不用汉字判断**：汉字是中日共用字符，不能作为日文残留依据。
    /// </summary>
    public static bool IsKana(char value)
        => value is (>= HiraganaStart and <= HiraganaEnd)
            or (>= KatakanaStart and <= KatakanaEnd)
            or (>= HalfwidthKatakanaStart and <= HalfwidthKatakanaEnd);

    /// <summary>文本中是否包含日文假名。</summary>
    public static bool ContainsKana(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (IsKana(ch))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>统计日文假名字符数量。</summary>
    public static int CountKana(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if (IsKana(ch))
            {
                count++;
            }
        }

        return count;
    }
}
