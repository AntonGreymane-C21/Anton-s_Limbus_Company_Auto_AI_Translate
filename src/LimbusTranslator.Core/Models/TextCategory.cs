namespace LimbusTranslator.Core.Models;

/// <summary>
/// 文件分类。按游戏目录结构划分，用于 Diff 分组与选择性汉化。
/// </summary>
public enum TextCategory
{
    /// <summary>一般文本（根目录 JSON，如 Enemies/Items/Skills 等）</summary>
    General,

    /// <summary>故事章节</summary>
    StoryData,

    /// <summary>角色语音</summary>
    PersonalityVoiceDlg,

    /// <summary>EGO 语音</summary>
    EGOVoiceDig,

    /// <summary>BGM 歌词</summary>
    BgmLyrics,

    /// <summary>播报员台词</summary>
    BattleAnnouncerDlg,

    /// <summary>
    /// RPG 玩法文本（第9.0C.23轮）：<c>RPGSystem/</c> 目录下的文件，
    /// 以及根目录中文件名（去掉语言前缀后）以 <c>RPG</c> 开头的文件（如 <c>RPGSuicideBoxUI.json</c>）。
    /// </summary>
    RpgSystem,
}

/// <summary>
/// 文本分类工具。
/// 根据相对文件路径首段判断分类。
/// </summary>
public static class TextCategoryHelper
{
    /// <summary>
    /// 根据相对路径获取分类。
    ///
    /// 入参可以是**逻辑路径**（<c>StoryData/1D101A.json</c>）或**物理路径**（<c>StoryData/EN_1D101A.json</c>）：
    /// 根级文件的语言前缀（<c>EN_</c>/<c>KR_</c>/<c>JP_</c>）会先被去掉，保证两种形式得到**同一个分类**
    ///（第9.0C.23轮：此前分类统计用物理路径、文件列表用逻辑路径，两者口径不一致）。
    /// </summary>
    public static TextCategory FromRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return TextCategory.General;
        }

        var normalized = StripLanguagePrefix(relativePath.Trim());
        var first = normalized.Split('/', '\\')[0];
        return first switch
        {
            "StoryData" => TextCategory.StoryData,
            "PersonalityVoiceDlg" => TextCategory.PersonalityVoiceDlg,
            "EGOVoiceDig" => TextCategory.EGOVoiceDig,
            "BgmLyrics" => TextCategory.BgmLyrics,
            "BattleAnnouncerDlg" => TextCategory.BattleAnnouncerDlg,
            "RPGSystem" => TextCategory.RpgSystem,
            // 根级 RPG*.json（如 RPGSuicideBoxUI.json）也归入 RPG 分类（第9.0C.23轮）
            _ => first.StartsWith("RPG", StringComparison.OrdinalIgnoreCase)
                ? TextCategory.RpgSystem
                : TextCategory.General,
        };
    }

    /// <summary>去掉**首个路径段**的语言前缀（EN_ / KR_ / JP_）；其它形式原样返回。</summary>
    private static string StripLanguagePrefix(string path)
    {
        if (path.StartsWith(SourceLanguageHelper.EnglishPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path[SourceLanguageHelper.EnglishPrefix.Length..];
        }

        if (path.StartsWith(SourceLanguageHelper.KoreanPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path[SourceLanguageHelper.KoreanPrefix.Length..];
        }

        if (path.StartsWith(SourceLanguageHelper.JapanesePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path[SourceLanguageHelper.JapanesePrefix.Length..];
        }

        return path;
    }

    /// <summary>
    /// 获取分类的中文显示名。
    /// </summary>
    public static string GetDisplayName(TextCategory category) => category switch
    {
        TextCategory.General => "一般文本",
        TextCategory.StoryData => "故事章节",
        TextCategory.PersonalityVoiceDlg => "角色语音",
        TextCategory.EGOVoiceDig => "EGO 语音",
        TextCategory.BgmLyrics => "BGM 歌词",
        TextCategory.BattleAnnouncerDlg => "播报员台词",
        TextCategory.RpgSystem => "RPG 玩法",
        _ => category.ToString(),
    };
}
