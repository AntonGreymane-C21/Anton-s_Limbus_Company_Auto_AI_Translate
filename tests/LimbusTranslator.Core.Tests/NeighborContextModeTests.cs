using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B 最终轮：**邻句上下文来源语言**按翻译模式选择（真实 Smoke 发现并修复）。
///
/// 背景：真实 Smoke 发现两点问题——
///   ① Smoke Harness 未接线 <c>TranslationContextBuilder</c> ⇒ 邻句上下文整体丢失；
///   ② 生产链把邻句索引固定构建自**英文**单元 ⇒ KR_ONLY 的请求里会出现英文邻句（违反 KR_ONLY 的纯韩文语义）。
/// 修复：来源语言唯一定义在 <see cref="TranslationModePolicy.GetNeighborSourceLanguage"/>，
/// 由 <see cref="ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits"/> 选单元，CLI / WPF / 测试共用。
/// </summary>
public sealed class NeighborContextModeTests
{
    private const string StoryFile = "StoryData/1D101A.json";

    [Theory]
    [InlineData(TranslationMode.EnglishOnly, SourceLanguage.English)]
    [InlineData(TranslationMode.KoreanEnglish, SourceLanguage.English)]
    [InlineData(TranslationMode.KoreanJapanese, SourceLanguage.Japanese)]
    [InlineData(TranslationMode.KoreanOnly, SourceLanguage.Korean)]
    public void 邻句来源语言按模式确定(TranslationMode mode, SourceLanguage expected)
        => Assert.Equal(expected, TranslationModePolicy.GetNeighborSourceLanguage(mode));

    [Fact]
    public void 邻句来源单元_KRONLY取韩文_KRJP取日文_英文模式取英文()
    {
        var english = Units("en text");
        var capture = Capture(new Dictionary<SourceLanguage, IReadOnlyList<TranslationUnit>>
        {
            [SourceLanguage.English] = english,
            [SourceLanguage.Korean] = Units("ko text"),
            [SourceLanguage.Japanese] = Units("ja text"),
        });

        Assert.Equal("en text", Single(ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(TranslationMode.EnglishOnly, capture, english)));
        Assert.Equal("en text", Single(ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(TranslationMode.KoreanEnglish, capture, english)));
        Assert.Equal("ja text", Single(ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(TranslationMode.KoreanJapanese, capture, english)));
        Assert.Equal("ko text", Single(ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(TranslationMode.KoreanOnly, capture, english)));
    }

    [Fact]
    public void 邻句来源单元_目标语言缺失时回退英文()
    {
        var english = Units("en text");
        var capture = Capture(new Dictionary<SourceLanguage, IReadOnlyList<TranslationUnit>>
        {
            [SourceLanguage.English] = english,
            [SourceLanguage.Korean] = Array.Empty<TranslationUnit>(),
        });

        // KR_ONLY 但韩文单元为空（目录缺失场景）⇒ 回退英文，保证上下文不整体丢失
        Assert.Equal("en text", Single(ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(TranslationMode.KoreanOnly, capture, english)));
        // capture 为 null（未捕获）⇒ 同样回退英文
        Assert.Equal("en text", Single(ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(TranslationMode.KoreanOnly, null, english)));
    }

    [Fact]
    public void 邻句索引_韩文来源时邻句文本为韩文()
    {
        var index = TranslationContextIndex.Build(new[]
        {
            Unit(5, "이전 문장", 0),
            Unit(6, "현재 문장", 1),
            Unit(7, "다음 문장", 2),
        });

        var next = index.FindNeighbor(Unit(6, "현재 문장", 1).Key.ToString(), NeighborRole.Next);

        Assert.NotNull(next);
        Assert.Equal("다음 문장", next!.SourceText);
    }

    [Fact]
    public void 邻句索引_日文来源时邻句文本为日文()
    {
        var index = TranslationContextIndex.Build(new[]
        {
            Unit(5, "前の文", 0),
            Unit(6, "今の文", 1),
            Unit(7, "次の文", 2),
        });

        var previous = index.FindNeighbor(Unit(6, "今の文", 1).Key.ToString(), NeighborRole.Previous);

        Assert.NotNull(previous);
        Assert.Equal("前の文", previous!.SourceText);
    }

    [Fact]
    public void 生产接线_CLI与WPF都按模式选择邻句来源()
    {
        var root = FindRepositoryRoot();
        var cli = File.ReadAllText(Path.Combine(root, "src", "LimbusTranslator.Cli", "Program.cs"));
        var wpf = File.ReadAllText(Path.Combine(root, "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(", cli);
        Assert.Contains("ProductionTranslationPlanBuilder.ResolveNeighborSourceUnits(", wpf);

        // 不得退回「固定用英文单元构建邻句索引」
        Assert.DoesNotContain("TranslationContextIndex.Build(result.NewUnits", cli);
    }

    private static string Single(IReadOnlyList<TranslationUnit> units) => Assert.Single(units).SourceText;

    private static TranslationUnit Unit(int recordId, string text, int order) => new()
    {
        Key = new UnitKey
        {
            RelativeFilePath = StoryFile,
            RecordId = recordId.ToString(),
            FieldPath = $"dataList[{order}].content",
        },
        FilePath = StoryFile,
        RecordId = recordId.ToString(),
        FieldPath = $"dataList[{order}].content",
        SourceText = text,
        Order = order,
    };

    private static IReadOnlyList<TranslationUnit> Units(string text)
        => new[] { Unit(6, text, 0) };

    private static MultilingualCaptureResult Capture(IReadOnlyDictionary<SourceLanguage, IReadOnlyList<TranslationUnit>> parsed)
        => new()
        {
            Success = true,
            ParsedUnits = parsed,
        };

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
