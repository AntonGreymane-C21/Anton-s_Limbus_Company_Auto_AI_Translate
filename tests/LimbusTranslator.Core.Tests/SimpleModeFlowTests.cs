using LimbusTranslator.Infrastructure.Presentation;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 简易模式：流程判定（四步）、审核一句话说明、下一条待审定位、界面模式偏好与接线守卫。
/// </summary>
public sealed class SimpleModeFlowTests
{
    private static SimpleFlowSnapshot Snapshot(
        bool gameReady = true,
        bool hasAnalysis = true,
        int needTranslate = 0,
        bool outputReady = false,
        int pendingReview = 0,
        bool reviewSkipped = false,
        bool changed = false,
        bool deployed = false)
        => new(gameReady, hasAnalysis, needTranslate, outputReady, pendingReview, reviewSkipped, changed, deployed);

    // ---------------- Resolve ----------------

    [Fact]
    public void 未找到游戏目录时_停在定位游戏()
    {
        Assert.Equal(SimpleStage.LocateGame, SimpleModeFlow.Resolve(Snapshot(gameReady: false, hasAnalysis: true, outputReady: true)));
    }

    [Fact]
    public void 已定位但未分析时_停在分析()
    {
        Assert.Equal(SimpleStage.Analyze, SimpleModeFlow.Resolve(Snapshot(hasAnalysis: false)));
    }

    [Fact]
    public void 分析后有待翻译文本_进入翻译()
    {
        Assert.Equal(SimpleStage.Translate, SimpleModeFlow.Resolve(Snapshot(needTranslate: 1284)));
    }

    [Fact]
    public void 分析后没有待翻译文本且未生成输出_显示已是最新()
    {
        Assert.Equal(SimpleStage.UpToDate, SimpleModeFlow.Resolve(Snapshot(needTranslate: 0)));
    }

    [Fact]
    public void 生成输出后有待审条目_进入审核()
    {
        Assert.Equal(SimpleStage.Review, SimpleModeFlow.Resolve(Snapshot(needTranslate: 10, outputReady: true, pendingReview: 3)));
    }

    [Fact]
    public void 选择稍后再审时_跳过审核直接部署()
    {
        Assert.Equal(
            SimpleStage.Deploy,
            SimpleModeFlow.Resolve(Snapshot(outputReady: true, pendingReview: 3, reviewSkipped: true)));
    }

    [Fact]
    public void 审核做过修改_必须先应用修改再部署()
    {
        Assert.Equal(SimpleStage.ApplyReview, SimpleModeFlow.Resolve(Snapshot(outputReady: true, changed: true)));
        // 即使用户跳过了剩余审核，已保存的修改也必须先应用
        Assert.Equal(
            SimpleStage.ApplyReview,
            SimpleModeFlow.Resolve(Snapshot(outputReady: true, pendingReview: 2, reviewSkipped: true, changed: true)));
    }

    [Fact]
    public void 审核完成后进入部署_部署后显示完成()
    {
        Assert.Equal(SimpleStage.Deploy, SimpleModeFlow.Resolve(Snapshot(outputReady: true)));
        Assert.Equal(SimpleStage.Done, SimpleModeFlow.Resolve(Snapshot(outputReady: true, deployed: true)));
    }

    // ---------------- 步骤条 ----------------

    [Fact]
    public void 步骤条固定四步()
    {
        Assert.Equal(new[] { "定位游戏", "分析", "翻译", "部署" }, SimpleModeFlow.StepTitles);
    }

    [Theory]
    [InlineData(SimpleStage.LocateGame, "A___")]
    [InlineData(SimpleStage.Analyze, "DA__")]
    [InlineData(SimpleStage.Translate, "DDA_")]
    [InlineData(SimpleStage.Review, "DDA_")]
    [InlineData(SimpleStage.ApplyReview, "DDA_")]
    [InlineData(SimpleStage.Deploy, "DDDA")]
    [InlineData(SimpleStage.UpToDate, "DDDD")]
    [InlineData(SimpleStage.Done, "DDDD")]
    public void 步骤状态与阶段对应(SimpleStage stage, string expected)
    {
        var actual = string.Concat(SimpleModeFlow.StepStates(stage).Select(state => state switch
        {
            SimpleStepState.Done => 'D',
            SimpleStepState.Active => 'A',
            _ => '_',
        }));

        Assert.Equal(expected, actual);
    }

    // ---------------- 一句话问题说明 ----------------

    [Fact]
    public void 问题说明优先显示阻塞问题并注明其余数量()
    {
        var issues = new[]
        {
            new ReviewIssueRow("警告", "术语不一致", "没有使用术语表里的译名。", "W1", false),
            new ReviewIssueRow("错误", "占位符缺失", "译文少了原文中的占位符。", "E1", true),
            new ReviewIssueRow("警告", "仍有英文残留", "译文里还留着成段英文。", "W2", false),
        };

        Assert.Equal("占位符缺失：译文少了原文中的占位符。（另有 2 个问题）", SimpleModeFlow.IssueLine(issues, "AI 原因"));
    }

    [Fact]
    public void 只有一个问题时不附加数量()
    {
        var issues = new[] { new ReviewIssueRow("警告", "仍有英文残留", "译文里还留着成段英文。", "W2", false) };

        Assert.Equal("仍有英文残留：译文里还留着成段英文。", SimpleModeFlow.IssueLine(issues, null));
    }

    [Fact]
    public void 没有校验问题时显示AI原因_都没有时显示通用提示()
    {
        Assert.Equal("AI 提示：语气可能不符合角色", SimpleModeFlow.IssueLine(Array.Empty<ReviewIssueRow>(), "  语气可能不符合角色 "));
        Assert.False(string.IsNullOrWhiteSpace(SimpleModeFlow.IssueLine(null, null)));
    }

    // ---------------- 下一条待审 ----------------

    [Fact]
    public void 下一条待审从当前之后开始并回绕()
    {
        var items = new[] { true, false, true, false };

        Assert.Equal(2, SimpleModeFlow.NextPendingIndex(items, 0, pending => pending));
        Assert.Equal(0, SimpleModeFlow.NextPendingIndex(items, 2, pending => pending));
        Assert.Equal(0, SimpleModeFlow.NextPendingIndex(items, -1, pending => pending));
    }

    [Fact]
    public void 没有其它待审条目时返回负一()
    {
        Assert.Equal(-1, SimpleModeFlow.NextPendingIndex(new[] { true, false }, 0, pending => pending));
        Assert.Equal(-1, SimpleModeFlow.NextPendingIndex(Array.Empty<bool>(), -1, pending => pending));
    }

    [Fact]
    public void 数量使用千分位()
    {
        Assert.Equal("1,284", SimpleModeFlow.FormatCount(1284));
        Assert.Equal("0", SimpleModeFlow.FormatCount(0));
    }

    // ---------------- 界面模式偏好 ----------------

    [Fact]
    public void 首次启动或文件损坏时默认简易模式()
    {
        var dir = NewTempDir();
        try
        {
            var path = UiModePreferences.GetPath(dir);
            Assert.Equal(UiMode.Simple, UiModePreferences.Load(path));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ not json");
            Assert.Equal(UiMode.Simple, UiModePreferences.Load(path));

            File.WriteAllText(path, "{ \"uiMode\": \"something\" }");
            Assert.Equal(UiMode.Simple, UiModePreferences.Load(path));

            File.WriteAllText(path, "{ \"uiMode\": 3 }");
            Assert.Equal(UiMode.Simple, UiModePreferences.Load(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void 记住上次选择并保留其它键()
    {
        var dir = NewTempDir();
        try
        {
            var path = UiModePreferences.GetPath(dir);
            Assert.True(UiModePreferences.Save(path, UiMode.Expert));
            Assert.Equal(UiMode.Expert, UiModePreferences.Load(path));
            Assert.Equal("data", new DirectoryInfo(Path.GetDirectoryName(path)!).Name);

            File.WriteAllText(path, "{ \"uiMode\": \"expert\", \"other\": 42 }");
            Assert.True(UiModePreferences.Save(path, UiMode.Simple));
            Assert.Equal(UiMode.Simple, UiModePreferences.Load(path));
            Assert.Contains("\"other\": 42", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("expert", UiMode.Expert)]
    [InlineData(" EXPERT ", UiMode.Expert)]
    [InlineData("simple", UiMode.Simple)]
    [InlineData(null, UiMode.Simple)]
    public void 模式文本解析(string? text, UiMode expected)
    {
        Assert.Equal(expected, UiModePreferences.Parse(text));
        Assert.Equal(expected, UiModePreferences.Parse(UiModePreferences.ToText(expected)));
    }

    // ---------------- 接线守卫 ----------------

    [Fact]
    public void 主窗口同时承载简易模式与专家模式()
    {
        var xaml = ReadSource("src", "LimbusTranslator.Wpf", "MainWindow.xaml");

        Assert.Contains("<views:SimpleModeView", xaml);
        Assert.Contains("Visibility=\"{Binding SimpleModeVisibility}\"", xaml);
        Assert.Contains("Visibility=\"{Binding ExpertModeVisibility}\"", xaml);
        Assert.Contains("Click=\"SwitchToSimpleMode_Click\"", xaml);
    }

    [Fact]
    public void 简易模式不包含文件勾选等专家功能_且暗色主题只作用于简易模式()
    {
        var simple = ReadSource("src", "LimbusTranslator.Wpf", "Views", "SimpleModeView.xaml");
        var app = ReadSource("src", "LimbusTranslator.Wpf", "App.xaml");

        Assert.DoesNotContain("FileTasks", simple);
        Assert.DoesNotContain("ReviewFilterOptions", simple);
        Assert.DoesNotContain("SelectedReviewEntry", simple);
        Assert.Contains("LimbusDarkTheme.xaml", simple);
        Assert.DoesNotContain("LimbusDarkTheme", app);
    }

    [Fact]
    public void 简易审核卡片只有通过并下一条与保存两个操作()
    {
        var simple = ReadSource("src", "LimbusTranslator.Wpf", "Views", "SimpleModeView.xaml");

        Assert.Contains("通过并下一条", simple);
        Assert.Contains("Click=\"SimpleSave_Click\"", simple);
        Assert.Contains("Click=\"SimpleApproveNext_Click\"", simple);
    }

    [Fact]
    public void 暗色主题资源键不重复()
    {
        // 重复 x:Key 只会在运行时加载 BAML 时崩溃（编译期不报错），这里提前拦截。
        var theme = ReadSource("src", "LimbusTranslator.Wpf", "Themes", "LimbusDarkTheme.xaml");
        var duplicates = System.Text.RegularExpressions.Regex.Matches(theme, "x:Key=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .GroupBy(key => key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void 界面模式偏好文件不进入版本库()
    {
        var gitignore = ReadSource(".gitignore");

        Assert.Contains("data/ui_prefs.json", gitignore);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "limbus_simple_mode_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string ReadSource(params string[] parts)
    {
        var path = Path.Combine(new[] { FindRepositoryRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"未找到源文件: {path}");
        return File.ReadAllText(path);
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
