using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.23轮：**RPG 独立分类** + **分类统计与文件列表同源**。
///
/// 真实反馈：
///   ① "有一些 RPG 开头的文件，想把它们做成单独分类"；
///   ② "播报员里有一个说需要汉化，工具没显示出来" —— 根因是分类统计用**文件级英文 Diff** 计数，
///      而「需要处理的文件」列表来自**生产计划**（KR 权威模式下，韩文未变 ⇒ 继承 ⇒ 不需要 AI）⇒ 两边矛盾。
/// </summary>
public sealed class CategoryStatisticsTests
{
    private static DiffEntry Entry(string file, TranslationAction action = TranslationAction.Inherit)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = "1", FieldPath = "dataList[0].content" },
            NewSourceText = "텍스트",
            DiffKind = DiffKind.Unchanged,
            Action = action,
        };

    private static ProductionTranslationPlan Plan(
        IReadOnlyList<DiffEntry> output,
        IReadOnlyList<DiffEntry> need)
        => new()
        {
            Mode = TranslationMode.KoreanEnglish,
            Candidates = output,
            NeedTranslate = need,
            PatchedCount = 0,
            InheritedKeptCount = 0,
            KoreanOnlyCount = 0,
            HasCanonicalCapture = true,
            AuthoritativeLanguage = SourceLanguage.Korean,
            AuthoritativeDirectory = "kr",
            IsKoreanAuthoritative = true,
            OutputEntries = output,
            ExpectedOutputKeys = output.Select(entry => entry.Key.ToString()).ToList(),
        };

    // ───────── ① RPG 独立分类 ─────────

    [Theory]
    [InlineData("RPGSystem/rpg-loc-dial.json")]
    [InlineData("RPGSystem/KR_rpg-loc-dial.json")]
    [InlineData("RPGSystem/EN_rpg-loc-dial.json")]
    [InlineData("RPGSuicideBoxUI.json")]
    [InlineData("EN_RPGSuicideBoxUI.json")]
    public void RPG文件归入RPG分类(string path)
        => Assert.Equal(TextCategory.RpgSystem, TextCategoryHelper.FromRelativePath(path));

    [Theory]
    [InlineData("StoryData/1D101A.json", TextCategory.StoryData)]
    [InlineData("StoryData/EN_1D101A.json", TextCategory.StoryData)]
    [InlineData("PersonalityVoiceDlg/x.json", TextCategory.PersonalityVoiceDlg)]
    [InlineData("EGOVoiceDig/x.json", TextCategory.EGOVoiceDig)]
    [InlineData("BgmLyrics/x.json", TextCategory.BgmLyrics)]
    [InlineData("BattleAnnouncerDlg/x.json", TextCategory.BattleAnnouncerDlg)]
    [InlineData("Items.json", TextCategory.General)]
    [InlineData("", TextCategory.General)]
    public void 其它分类不受影响(string path, TextCategory expected)
        => Assert.Equal(expected, TextCategoryHelper.FromRelativePath(path));

    [Fact]
    public void RPG分类有中文显示名()
        => Assert.Equal("RPG 玩法", TextCategoryHelper.GetDisplayName(TextCategory.RpgSystem));

    [Fact]
    public void RPG文件被统计到RPG分类而不是一般文本()
    {
        var output = new[]
        {
            Entry("RPGSystem/rpg-loc-dial.json"),
            Entry("RPGSuicideBoxUI.json"),
            Entry("Items.json"),
        };

        var rows = CategoryStatistics.Build(Plan(output, Array.Empty<DiffEntry>()));

        Assert.Equal(2, rows.Single(row => row.Category == TextCategory.RpgSystem).TotalFileCount);
        Assert.Equal(1, rows.Single(row => row.Category == TextCategory.General).TotalFileCount);
    }

    // ───────── ② 统计与文件列表同源 ─────────

    [Fact]
    public void 仅参考变化的文件不计入需译_而是通过提示解释()
    {
        var output = new[]
        {
            Entry("BattleAnnouncerDlg/x.json"),                     // 英文变了、韩文未变 ⇒ 计划判 Inherit
            Entry("StoryData/a.json", TranslationAction.TranslateModified),
        };
        var need = new[] { Entry("StoryData/a.json", TranslationAction.TranslateModified) };
        var kinds = new Dictionary<string, FileDiffKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["BattleAnnouncerDlg/x.json"] = FileDiffKind.Modified,
            ["StoryData/a.json"] = FileDiffKind.Modified,
        };

        var rows = CategoryStatistics.Build(Plan(output, need), kinds);

        var announcer = rows.Single(row => row.Category == TextCategory.BattleAnnouncerDlg);
        Assert.Equal(1, announcer.TotalFileCount);
        Assert.Equal(0, announcer.NeedTranslateFileCount);      // ★ 与「需要处理的文件」一致
        Assert.Equal(1, announcer.ReferenceOnlyFileCount);      // ★ 用"仅参考变化"解释原因
        Assert.Contains("仅参考变化 1", announcer.ReferenceOnlyNote);

        var story = rows.Single(row => row.Category == TextCategory.StoryData);
        Assert.Equal(1, story.NeedTranslateFileCount);
        Assert.Equal(0, story.ReferenceOnlyFileCount);
    }

    [Fact]
    public void 未变化的文件不计入参考变化()
    {
        var output = new[] { Entry("StoryData/a.json") };
        var kinds = new Dictionary<string, FileDiffKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["StoryData/a.json"] = FileDiffKind.Unchanged,
        };

        var rows = CategoryStatistics.Build(Plan(output, Array.Empty<DiffEntry>()), kinds);

        Assert.Equal(0, rows.Single(row => row.Category == TextCategory.StoryData).ReferenceOnlyFileCount);
    }

    [Fact]
    public void 所有分类都会出现在统计里_含零文件分类()
    {
        var rows = CategoryStatistics.Build(Plan(new[] { Entry("StoryData/a.json") }, Array.Empty<DiffEntry>()));

        Assert.Equal(Enum.GetValues<TextCategory>().Length, rows.Count);
        Assert.Equal(0, rows.Single(row => row.Category == TextCategory.BgmLyrics).TotalFileCount);
    }

    [Fact]
    public void 界面分类统计必须走同一实现并显示参考变化提示()
    {
        var viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs"));
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "LimbusTranslator.Wpf", "MainWindow.xaml"));

        Assert.Contains("CategoryStatistics.Build(plan, BuildFileDiffKindMap())", viewModel);
        Assert.Contains("ReferenceOnlyFileCount = row.ReferenceOnlyFileCount", viewModel);
        Assert.Contains("Text=\"{Binding ReferenceOnlyNote}\"", xaml);
        Assert.Contains("本轮按继承处理（不会调用 AI）", viewModel);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LimbusTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（LimbusTranslator.slnx）");
    }
}
