using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace LimbusTranslator.Core.CharacterStyle;

/// <summary>
/// 角色基本信息。
/// </summary>
/// <param name="StyleKey">character_styles.json 的英文键（如 "Gregor"）</param>
/// <param name="ChineseName">标准中文名（如 "格里高尔"）</param>
/// <param name="KoreanName">标准韩文名（如 "그레고르"）</param>
public sealed record CharacterInfo(
    string StyleKey,
    string ChineseName,
    string KoreanName);

/// <summary>
/// 12 罪人与但丁的角色别名映射助手。
///
/// 支持：
///   1. 12 罪人 + 단테（但丁）
///   2. 韩文 model ID（含带数字后缀如 료슈2）、英文名、中文名相互映射
///   3. 统一归一化为中文名以及 character_styles.json 的 key
/// </summary>
public static class CharacterAliasHelper
{
    private static readonly Regex SuffixRegex = new(@"[\s_\-#]*\d+$", RegexOptions.Compiled);

    private static readonly List<CharacterInfo> CharacterList = new()
    {
        new CharacterInfo("Yi Sang", "李箱", "이상"),
        new CharacterInfo("Faust", "浮士德", "파우스트"),
        new CharacterInfo("Don Quixote", "堂吉诃德", "돈키호테"),
        new CharacterInfo("Ryoshu", "良秀", "료슈"),
        new CharacterInfo("Meursault", "默尔索", "뫼르소"),
        new CharacterInfo("Hong Lu", "鸿璐", "홍루"),
        new CharacterInfo("Heathcliff", "希斯克利夫", "히스클리프"),
        new CharacterInfo("Ishmael", "以实玛利", "이스마엘"),
        new CharacterInfo("Rodion", "罗佳", "로쟈"),
        new CharacterInfo("Sinclair", "辛克莱", "싱클레어"),
        new CharacterInfo("Outis", "奥提斯", "오티스"),
        new CharacterInfo("Gregor", "格里高尔", "그레고르"),
        new CharacterInfo("Dante", "但丁", "단테"),
    };

    private static readonly Dictionary<string, CharacterInfo> AliasMap = new(StringComparer.OrdinalIgnoreCase);

    static CharacterAliasHelper()
    {
        void Register(CharacterInfo info, params string[] aliases)
        {
            AliasMap[info.StyleKey] = info;
            AliasMap[info.ChineseName] = info;
            AliasMap[info.KoreanName] = info;
            foreach (var alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                {
                    AliasMap[alias] = info;
                }
            }
        }

        Register(CharacterList[0], "YiSang", "Yi_Sang");
        Register(CharacterList[1]);
        Register(CharacterList[2], "DonQuixote", "Don_Quixote", "唐吉诃德");
        Register(CharacterList[3], "RyoShu");
        Register(CharacterList[4]);
        Register(CharacterList[5], "HongLu", "Hong_Lu");
        Register(CharacterList[6]);
        Register(CharacterList[7]);
        Register(CharacterList[8], "Rodya", "로디온");
        Register(CharacterList[9]);
        Register(CharacterList[10]);
        Register(CharacterList[11]);
        Register(CharacterList[12]);
    }

    /// <summary>所有支持的已知角色（12 罪人 + 但丁）</summary>
    public static IReadOnlyList<CharacterInfo> Characters => CharacterList;

    /// <summary>
    /// 解析输入角色名/别名（韩文 model / 英文名 / 中文名 / 带数字后缀等）。
    /// </summary>
    public static bool TryResolve(string? rawSpeaker, [NotNullWhen(true)] out CharacterInfo? character)
    {
        character = null;
        if (string.IsNullOrWhiteSpace(rawSpeaker))
        {
            return false;
        }

        var trimmed = rawSpeaker.Trim();
        if (AliasMap.TryGetValue(trimmed, out character))
        {
            return true;
        }

        // 去除数字后缀（如 료슈2, 료슈_2, Ryoshu2, 그레고르1）
        var stripped = SuffixRegex.Replace(trimmed, string.Empty);
        if (!string.IsNullOrEmpty(stripped) && AliasMap.TryGetValue(stripped, out character))
        {
            return true;
        }

        // 紧凑匹配（去掉空格、下划线、连字符）
        var compact = trimmed.Replace(" ", "").Replace("_", "").Replace("-", "");
        if (!string.IsNullOrEmpty(compact))
        {
            if (AliasMap.TryGetValue(compact, out character))
            {
                return true;
            }

            var compactStripped = SuffixRegex.Replace(compact, string.Empty);
            if (!string.IsNullOrEmpty(compactStripped) && AliasMap.TryGetValue(compactStripped, out character))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取角色的标准中文名（如 "그레고르" / "Gregor" → "格里高尔"）。
    /// </summary>
    public static bool TryGetChineseName(string? rawSpeaker, [NotNullWhen(true)] out string? chineseName)
    {
        if (TryResolve(rawSpeaker, out var character))
        {
            chineseName = character.ChineseName;
            return true;
        }

        chineseName = null;
        return false;
    }

    /// <summary>
    /// 获取 character_styles.json 的 key（如 "格里高尔" / "그레고르" → "Gregor"）。
    /// </summary>
    public static bool TryGetStyleKey(string? rawSpeaker, [NotNullWhen(true)] out string? styleKey)
    {
        if (TryResolve(rawSpeaker, out var character))
        {
            styleKey = character.StyleKey;
            return true;
        }

        styleKey = null;
        return false;
    }

    /// <summary>
    /// 获取角色的标准韩文名（如 "格里高尔" / "Gregor" → "그레고르"）。
    /// </summary>
    public static bool TryGetKoreanName(string? rawSpeaker, [NotNullWhen(true)] out string? koreanName)
    {
        if (TryResolve(rawSpeaker, out var character))
        {
            koreanName = character.KoreanName;
            return true;
        }

        koreanName = null;
        return false;
    }

    /// <summary>
    /// 转换为中文名；若不是已知罪人/但丁别名，则返回原串。
    /// </summary>
    public static string NormalizeToChinese(string? rawSpeaker)
    {
        if (TryGetChineseName(rawSpeaker, out var chineseName))
        {
            return chineseName;
        }

        return rawSpeaker ?? string.Empty;
    }

    /// <summary>
    /// 判断是否为 12 罪人或但丁。
    /// </summary>
    public static bool IsSinnerOrDante(string? rawSpeaker) => TryResolve(rawSpeaker, out _);

    /// <summary>
    /// 去除名称末尾的数字与分隔符后缀（如 "료슈2" → "료슈"）。
    /// </summary>
    public static string NormalizeRawName(string? rawSpeaker)
    {
        if (string.IsNullOrWhiteSpace(rawSpeaker))
        {
            return string.Empty;
        }

        var trimmed = rawSpeaker.Trim();
        var stripped = SuffixRegex.Replace(trimmed, string.Empty);
        return string.IsNullOrEmpty(stripped) ? trimmed : stripped;
    }
}
