using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C轮：GUI 展示层与工作流状态的自动化验收（不依赖真实窗口/点击）。
///
/// 覆盖：模式文案与持久化、四模式审核展示规则（权威 / 参考 / 不使用）、
/// 邻句语言标签、Validator 中文说明、模型自报 needs_review 与 Validator 分离、
/// 门禁中文映射、运行期按钮状态与模式锁定、部署确认与失败状态。
/// </summary>
public sealed class GuiPresentationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(), "limbus_gui_tests_" + Guid.NewGuid().ToString("N")[..8]);

    public GuiPresentationTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结果
        }
    }

    // ---------- 1. 模式文案 ↔ 枚举映射 ----------

    [Fact]
    public void 模式文案_四个模式都有中文名称与说明()
    {
        var all = TranslationModePresentation.All;

        Assert.Equal(4, all.Count);
        Assert.Equal(
            new[] { TranslationMode.EnglishOnly, TranslationMode.KoreanEnglish, TranslationMode.KoreanJapanese, TranslationMode.KoreanOnly },
            all.Select(option => option.Mode).ToArray());
        Assert.All(all, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(option.Subtitle));
            Assert.False(string.IsNullOrWhiteSpace(option.Description));
            Assert.False(string.IsNullOrWhiteSpace(option.InternalCode));
        });
    }

    [Fact]
    public void 模式文案_只有KREN标记推荐且不写夸张结论()
    {
        var recommended = TranslationModePresentation.All.Where(option => option.IsRecommended).ToList();

        Assert.Single(recommended);
        Assert.Equal(TranslationMode.KoreanEnglish, recommended[0].Mode);
        Assert.All(
            TranslationModePresentation.All,
            option => Assert.DoesNotContain("最佳", option.Description));
        Assert.Contains("推荐", TranslationModePresentation.Resolve(TranslationMode.KoreanEnglish).Headline);
    }

    [Theory]
    [InlineData(TranslationMode.EnglishOnly, "英文", "仅使用官方英文文本")]
    [InlineData(TranslationMode.KoreanEnglish, "韩文 + 英文", "以韩文原文为准，英文作为辅助参考")]
    [InlineData(TranslationMode.KoreanJapanese, "韩文 + 日文", "以韩文原文为准，日文作为辅助参考")]
    [InlineData(TranslationMode.KoreanOnly, "仅韩文", "直接根据韩文原文翻译")]
    public void 模式文案_名称与副标题稳定(TranslationMode mode, string shortName, string subtitle)
    {
        Assert.Equal(shortName, TranslationModePresentation.ShortName(mode));
        Assert.Equal(subtitle, TranslationModePresentation.Resolve(mode).Subtitle);
    }

    // ---------- 2/3/4. 模式配置：读取 / 保存 / 往返 ----------

    [Fact]
    public void 模式配置_默认ENONLY_保存后可往返读取()
    {
        // 未写文件 ⇒ 默认 EN_ONLY
        Assert.True(AppSettingsLoader.TryLoadTranslationMode(_tempDir, out var initial, out var error), error);
        Assert.Equal(TranslationMode.EnglishOnly, initial);

        AppSettingsLoader.SaveTranslationMode(_tempDir, TranslationMode.KoreanJapanese);

        Assert.True(AppSettingsLoader.TryLoadTranslationMode(_tempDir, out var saved, out var error2), error2);
        Assert.Equal(TranslationMode.KoreanJapanese, saved);
    }

    [Fact]
    public void 模式配置_保存模式不会丢失其它配置段落()
    {
        var path = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(
            path,
            """
            {
              "provider": "deepseek",
              "deepSeek": { "ApiKey": "sk-test", "Model": "deepseek-v4-flash" },
              "batch": { "maxItemsPerBatch": 12 },
              "paths": { "game": "D:\\Game" }
            }
            """);

        AppSettingsLoader.SaveTranslationMode(_tempDir, TranslationMode.KoreanOnly);

        var text = File.ReadAllText(path);
        Assert.Contains("deepseek-v4-flash", text);
        Assert.Contains("maxItemsPerBatch", text);
        Assert.Contains("D:\\\\Game", text);
        Assert.Contains("kr_only", text);
    }

    [Fact]
    public void 模式配置_保存时清理旧字段避免走迁移分支()
    {
        var path = Path.Combine(_tempDir, "appsettings.json");
        File.WriteAllText(path, """{ "translationSourceLanguage": "ko" }""");

        AppSettingsLoader.SaveTranslationMode(_tempDir, TranslationMode.KoreanEnglish);

        var text = File.ReadAllText(path);
        Assert.DoesNotContain("translationSourceLanguage", text);
        Assert.True(AppSettingsLoader.TryLoadTranslationMode(_tempDir, out var mode, out _));
        Assert.Equal(TranslationMode.KoreanEnglish, mode);
    }

    // ---------- 5~10. 四模式审核展示规则 ----------

    [Fact]
    public void 审核展示_ENONLY以英文为权威且不使用韩文日文()
    {
        var entry = Entry(TranslationMode.EnglishOnly, selected: "Deal +10% damage", canonicalKorean: "그림 재료 +10%");
        var model = ReviewDisplayModel.Build(entry, TranslationMode.EnglishOnly, Sources(), snapshot: null);

        Assert.Equal("英文原文", model.PrimarySource.Label);
        Assert.Equal("Deal +10% damage", model.PrimarySource.Text);
        Assert.Null(model.ReferenceSource);
        Assert.Contains("不使用参考译文", model.ReferencePanelNote);
        Assert.Equal(2, model.OtherLanguages.Count);                       // 韩文 + 日文（折叠参考）
        Assert.All(model.OtherLanguages, block => Assert.Contains("不参与翻译", block.Label));
        Assert.Equal("上下文语言：英文", model.NeighborLanguageLabel);
        Assert.Equal("英文", model.EffectiveLanguageText);
    }

    [Fact]
    public void 审核展示_KREN韩文权威加英文参考()
    {
        var entry = Entry(TranslationMode.KoreanEnglish, selected: "Deal +10% damage", canonicalKorean: "그림 재료 +10%");
        var model = ReviewDisplayModel.Build(entry, TranslationMode.KoreanEnglish, Sources(), snapshot: null);

        Assert.Equal("韩文原文（权威）", model.PrimarySource.Label);
        Assert.Equal("그림 재료 +10%", model.PrimarySource.Text);
        Assert.NotNull(model.ReferenceSource);
        Assert.Equal("英文参考", model.ReferenceSource!.Label);
        Assert.Equal("仅作参考；与韩文原文冲突时以韩文为准", model.ReferenceSource.Note);
        Assert.Single(model.OtherLanguages);                                // 仅日文进其它参考
        Assert.Equal("日文参考（该模式不参与翻译）", model.OtherLanguages[0].Label);
        Assert.Equal("上下文语言：英文", model.NeighborLanguageLabel);
    }

    [Fact]
    public void 审核展示_KRJP日文参考且邻句语言为日文()
    {
        var entry = Entry(TranslationMode.KoreanJapanese, selected: "絵の材料 +10%", canonicalKorean: "그림 재료 +10%");
        var model = ReviewDisplayModel.Build(entry, TranslationMode.KoreanJapanese, Sources(), snapshot: null);

        Assert.Equal("韩文原文（权威）", model.PrimarySource.Label);
        Assert.Equal("日文参考", model.ReferenceSource!.Label);
        Assert.Equal("上下文语言：日文", model.NeighborLanguageLabel);
        Assert.Equal("日文", model.EffectiveLanguageText);
    }

    [Fact]
    public void 审核展示_KRONLY仅韩文且英文日文只作折叠参考()
    {
        var entry = Entry(TranslationMode.KoreanOnly, selected: "그림 재료 +10%", canonicalKorean: "그림 재료 +10%");
        var model = ReviewDisplayModel.Build(entry, TranslationMode.KoreanOnly, Sources(), snapshot: null);

        Assert.Equal("韩文原文（权威）", model.PrimarySource.Label);
        Assert.Null(model.ReferenceSource);
        Assert.Contains("仅根据韩文原文翻译", model.ReferencePanelNote);
        Assert.Equal(2, model.OtherLanguages.Count);
        Assert.Equal("上下文语言：韩文", model.NeighborLanguageLabel);
        Assert.Equal("韩文", model.EffectiveLanguageText);
    }

    [Fact]
    public void 审核展示_缺少韩文原文时给出明确提示()
    {
        var entry = Entry(TranslationMode.KoreanOnly, selected: "그림 재료 +10%", canonicalKorean: null);
        entry.RunTranslationMode = TranslationMode.KoreanOnly;

        var model = ReviewDisplayModel.Build(entry, TranslationMode.KoreanOnly, Sources(), snapshot: null);

        Assert.False(model.PrimarySource.HasText);
        Assert.Equal("（无）", model.PrimarySource.DisplayText);
        Assert.Contains("缺少韩文原文", model.PrimarySource.Note);
    }

    [Fact]
    public void 审核展示_StoryData显示Speaker与只读邻句()
    {
        var entry = Entry(TranslationMode.KoreanEnglish, selected: "No, it’s fine.", canonicalKorean: "아니에요.", storyData: true, speaker: "유리");
        var context = new TranslationContext
        {
            Previous = new NeighborContextEntry
            {
                Role = NeighborRole.Previous,
                SourceText = "이전 문장",
                UnitKey = "k1",
                RecordId = "5",
                FieldPath = "dataList[0].content",
                SequenceIndex = 0,
            },
            Next = new NeighborContextEntry
            {
                Role = NeighborRole.Next,
                SourceText = "다음 문장",
                UnitKey = "k2",
                RecordId = "7",
                FieldPath = "dataList[2].content",
                SequenceIndex = 2,
            },
            ContextScopeKey = "StoryData/1D101A.json",
        };

        var model = ReviewDisplayModel.Build(entry, TranslationMode.KoreanEnglish, Sources(), context, snapshot: null);

        Assert.True(model.IsStoryData);
        Assert.Equal("유리", model.SpeakerText);
        Assert.Equal("이전 문장", model.PreviousText);
        Assert.Equal("다음 문장", model.NextText);
        Assert.Contains("运行前快照", model.NeighborPanelNote);
        Assert.Equal("上下文语言：英文", model.NeighborLanguageLabel);
    }

    [Fact]
    public void 审核展示_非StoryData明确说明没有邻句()
    {
        var entry = Entry(TranslationMode.KoreanEnglish, selected: "Deal +10% damage", canonicalKorean: "그림 재료 +10%");
        var model = ReviewDisplayModel.Build(entry, TranslationMode.KoreanEnglish, null, null, snapshot: null);

        Assert.False(model.IsStoryData);
        Assert.Contains("不是 StoryData", model.NeighborPanelNote);

        var jp = ReviewDisplayModel.Build(
            Entry(TranslationMode.KoreanJapanese, "x", "y"),
            TranslationMode.KoreanJapanese,
            null,
            null,
            null);
        Assert.Contains("没有取到日文文本", jp.ReferenceSource!.Note);
    }

    // ---------- 11/12. Validator 中文 + 模型自报分离 ----------

    [Fact]
    public void 校验问题_代码映射为中文名称与说明()
    {
        Assert.Equal("仍有韩文残留", WorkflowText.Issue(ValidationIssueCodes.KoreanResidue).Name);
        Assert.Contains("韩文", WorkflowText.Issue(ValidationIssueCodes.KoreanResidue).Explanation);
        Assert.Equal("仍有日文假名残留", WorkflowText.Issue(ValidationIssueCodes.JapaneseResidue).Name);
        Assert.Equal("术语可能未按指定译法", WorkflowText.Issue(ValidationIssueCodes.TerminologyMismatch).Name);
        Assert.Equal("缺少韩文原文", WorkflowText.Issue(ValidationIssueCodes.CanonicalKoreanSourceMissing).Name);
        Assert.Equal("错误", WorkflowText.Severity(ValidationSeverity.Error));
        Assert.Equal("警告", WorkflowText.Severity(ValidationSeverity.Warning));
        Assert.True(WorkflowText.IsBlocking(ValidationSeverity.Error));
        Assert.False(WorkflowText.IsBlocking(ValidationSeverity.Warning));
        Assert.Contains(
            ValidationIssueCodes.KoreanResidue,
            WorkflowText.IssueTechnicalLine(ValidationIssueCodes.KoreanResidue));
    }

    [Fact]
    public void 审核展示_模型自报需确认与Validator问题分开呈现()
    {
        var entry = Entry(TranslationMode.KoreanEnglish, selected: "Art Medium", canonicalKorean: "그림 재료");
        entry.NeedsReview = true;
        entry.ReviewReason = "\"Art Medium\" 暂无锁定译法，建议确认社区标准译法。";

        var model = ReviewDisplayModel.Build(entry, TranslationMode.KoreanEnglish, Sources(), null, snapshot: null);

        Assert.NotNull(model.AiReviewReason);
        Assert.Contains("Art Medium", model.AiReviewReason!);
        Assert.Empty(model.Issues);                                   // Validator 没有报问题
        Assert.Equal("未发现校验问题", model.IssuePanelNote);
        Assert.Equal("需要人工确认", model.ReviewStateText);           // 但状态是「需要人工确认」
    }

    [Fact]
    public void 审核展示_Validator问题带级别与阻塞标记()
    {
        var entry = Entry(TranslationMode.KoreanOnly, selected: "그림 재료", canonicalKorean: "그림 재료");
        entry.ValidationIssues = new[]
        {
            new ValidationIssue
            {
                Key = entry.Key,
                Code = ValidationIssueCodes.KoreanResidue,
                Severity = ValidationSeverity.Warning,
                Category = ValidationCategory.Language,
                Validator = "KoreanResidueValidator",
                Message = "译文中检测到韩文字符残留",
            },
        };

        var model = ReviewDisplayModel.Build(entry, TranslationMode.KoreanOnly, Sources(), null, snapshot: null);

        var issue = Assert.Single(model.Issues);
        Assert.Equal("警告", issue.SeverityText);
        Assert.Equal("仍有韩文残留", issue.Name);
        Assert.False(issue.IsBlocking);
        Assert.Contains("警告｜仍有韩文残留", issue.Headline);
        Assert.Contains("共 1 项", model.IssuePanelNote);
    }

    // ---------- 测试辅助 ----------

    private static DiffEntry Entry(
        TranslationMode mode,
        string? selected,
        string? canonicalKorean,
        bool storyData = false,
        string? speaker = null)
    {
        var file = storyData ? "StoryData/1D101A.json" : "Bufs_Mirror7.json";
        var field = storyData ? "dataList[1].content" : "dataList[0].desc";
        return new DiffEntry
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = storyData ? "6" : "Inspire", FieldPath = field },
            NewSourceText = selected,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
            Translation = "测试译文",
            Speaker = speaker,
            CanonicalKoreanText = canonicalKorean,
            RunTranslationMode = mode,
            EffectiveSourceLanguage = mode switch
            {
                TranslationMode.KoreanOnly => SourceLanguage.Korean,
                TranslationMode.KoreanJapanese => SourceLanguage.Japanese,
                _ => SourceLanguage.English,
            },
        };
    }

    private static MultilingualUnitSources Sources() => new()
    {
        Korean = "그림 재료 +10%",
        English = "Deal +10% damage",
        Japanese = "絵の材料 +10%",
    };
}



