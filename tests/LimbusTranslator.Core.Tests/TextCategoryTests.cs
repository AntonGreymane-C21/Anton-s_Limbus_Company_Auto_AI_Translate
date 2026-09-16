using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 文本分类测试。
/// </summary>
public class TextCategoryTests
{
    [Theory]
    [InlineData("StoryData/EN_1D101A.json", TextCategory.StoryData)]
    [InlineData("PersonalityVoiceDlg/EN_Voice_1.json", TextCategory.PersonalityVoiceDlg)]
    [InlineData("EGOVoiceDig/EN_Ego_1.json", TextCategory.EGOVoiceDig)]
    [InlineData("BgmLyrics/BgmLyrics_10948.json", TextCategory.BgmLyrics)]
    [InlineData("BattleAnnouncerDlg/EN_Announcer_1.json", TextCategory.BattleAnnouncerDlg)]
    [InlineData("EN_Enemies.json", TextCategory.General)]
    public void 分类_按路径首段正确识别(string path, TextCategory expected)
    {
        Assert.Equal(expected, TextCategoryHelper.FromRelativePath(path));
    }

    [Fact]
    public void 分类显示名_非空且正确()
    {
        Assert.Equal("故事章节", TextCategoryHelper.GetDisplayName(TextCategory.StoryData));
        Assert.Equal("角色语音", TextCategoryHelper.GetDisplayName(TextCategory.PersonalityVoiceDlg));
        Assert.Equal("EGO 语音", TextCategoryHelper.GetDisplayName(TextCategory.EGOVoiceDig));
        Assert.Equal("BGM 歌词", TextCategoryHelper.GetDisplayName(TextCategory.BgmLyrics));
        Assert.Equal("播报员台词", TextCategoryHelper.GetDisplayName(TextCategory.BattleAnnouncerDlg));
        Assert.Equal("一般文本", TextCategoryHelper.GetDisplayName(TextCategory.General));
    }
}
