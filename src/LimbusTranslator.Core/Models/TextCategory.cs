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
}

/// <summary>
/// 文本分类工具。
/// 根据相对文件路径首段判断分类。
/// </summary>
public static class TextCategoryHelper
{
    /// <summary>
    /// 根据相对路径获取分类。
    /// </summary>
    public static TextCategory FromRelativePath(string relativePath)
    {
        var first = relativePath.Split('/', '\\')[0];
        return first switch
        {
            "StoryData" => TextCategory.StoryData,
            "PersonalityVoiceDlg" => TextCategory.PersonalityVoiceDlg,
            "EGOVoiceDig" => TextCategory.EGOVoiceDig,
            "BgmLyrics" => TextCategory.BgmLyrics,
            "BattleAnnouncerDlg" => TextCategory.BattleAnnouncerDlg,
            _ => TextCategory.General,
        };
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
        _ => category.ToString(),
    };
}
