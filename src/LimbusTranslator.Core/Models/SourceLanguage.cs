namespace LimbusTranslator.Core.Models;

/// <summary>
/// 翻译依据的源语言（第9.0轮）。
///
/// 系统语义（重要）：
///   - <b>Canonical Original = 韩文（Korean）</b>：游戏原始文本，内容是否变化以韩文为准；
///   - <b>Translation Source = 用户选择的源语言</b>（en / ja / ko），是本次主要翻译依据；
///   - <b>Target = 简体中文</b>。
///
/// 无论选择哪种源语言，韩文原文始终作为**权威参考**提供给模型（选择韩文时即为主依据，不重复发送）。
/// </summary>
public enum SourceLanguage
{
    /// <summary>韩文（游戏原始文本 / Canonical）</summary>
    Korean = 0,

    /// <summary>英文（官方译本）</summary>
    English = 1,

    /// <summary>日文（官方译本）</summary>
    Japanese = 2,
}

/// <summary>源语言工具（配置码 / 显示名 / 文件名前缀），避免魔法字符串散落各处。</summary>
public static class SourceLanguageHelper
{
    /// <summary>韩文文件前缀</summary>
    public const string KoreanPrefix = "KR_";

    /// <summary>英文文件前缀</summary>
    public const string EnglishPrefix = "EN_";

    /// <summary>日文文件前缀</summary>
    public const string JapanesePrefix = "JP_";

    /// <summary>默认翻译依据（保留现有英文用户习惯）</summary>
    public const SourceLanguage Default = SourceLanguage.English;

    /// <summary>配置码：ko / en / ja。</summary>
    public static string ToCode(SourceLanguage language) => language switch
    {
        SourceLanguage.Korean => "ko",
        SourceLanguage.Japanese => "ja",
        _ => "en",
    };

    /// <summary>显示名。</summary>
    public static string GetDisplayName(SourceLanguage language) => language switch
    {
        SourceLanguage.Korean => "韩文（原文）",
        SourceLanguage.Japanese => "日文",
        _ => "英文",
    };

    /// <summary>文件名前缀。</summary>
    public static string GetFilePrefix(SourceLanguage language) => language switch
    {
        SourceLanguage.Korean => KoreanPrefix,
        SourceLanguage.Japanese => JapanesePrefix,
        _ => EnglishPrefix,
    };

    /// <summary>游戏目录中的语言子目录名（Localize/{en|kr|jp}）。</summary>
    public static string GetLocalizeDirectoryName(SourceLanguage language) => language switch
    {
        SourceLanguage.Korean => "kr",
        SourceLanguage.Japanese => "jp",
        _ => "en",
    };

    /// <summary>解析配置码（无法识别 → null，由调用方决定默认值/报错）。</summary>
    public static SourceLanguage? TryParseCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim().ToLowerInvariant() switch
        {
            "ko" or "kr" => SourceLanguage.Korean,
            "en" => SourceLanguage.English,
            "ja" or "jp" => SourceLanguage.Japanese,
            _ => null,
        };
    }

    /// <summary>全部支持的源语言（固定顺序：韩文 → 英文 → 日文）。</summary>
    public static IReadOnlyList<SourceLanguage> All { get; } = new[]
    {
        SourceLanguage.Korean,
        SourceLanguage.English,
        SourceLanguage.Japanese,
    };
}