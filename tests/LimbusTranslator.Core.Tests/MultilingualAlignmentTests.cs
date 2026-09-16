using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Multilingual;
using LimbusTranslator.Infrastructure.Parsing;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0轮：语言文件映射（前缀归一化）与三语对齐 / Fallback 规则。</summary>
public sealed class MultilingualAlignmentTests
{
    private static TranslationUnit Unit(string file, string recordId, string field, string? text, string? speaker = null)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = recordId, FieldPath = field },
            SourceText = text ?? string.Empty,
            RecordId = recordId,
            FieldPath = field,
            FilePath = file,
            Speaker = speaker,
        };

    [Theory]
    [InlineData("EN_Items.json", "Items.json")]
    [InlineData("KR_Items.json", "Items.json")]
    [InlineData("JP_Items.json", "Items.json")]
    [InlineData("StoryData/EN_1D101A.json", "StoryData/1D101A.json")]
    [InlineData("StoryData/KR_1D101A.json", "StoryData/1D101A.json")]
    [InlineData("StoryData/JP_1D101A.json", "StoryData/1D101A.json")]
    [InlineData("Items.json", "Items.json")]
    public void 任意语言前缀_归一化为同一逻辑路径(string physical, string expected)
        => Assert.Equal(expected, LanguageFileMapper.ToCanonicalRelativePath(physical));

    [Theory]
    [InlineData(SourceLanguage.Korean, "Items.json", "KR_Items.json")]
    [InlineData(SourceLanguage.English, "Items.json", "EN_Items.json")]
    [InlineData(SourceLanguage.Japanese, "Items.json", "JP_Items.json")]
    [InlineData(SourceLanguage.Japanese, "StoryData/1D101A.json", "StoryData/JP_1D101A.json")]
    [InlineData(SourceLanguage.Korean, "EN_Items.json", "KR_Items.json")]
    public void 逻辑路径_按语言生成物理路径(SourceLanguage language, string canonical, string expected)
        => Assert.Equal(expected, LanguageFileMapper.ToPhysicalRelativePath(language, canonical));

    [Fact]
    public void 三语前缀_往返归一化稳定()
    {
        const string canonical = "StoryData/1D101A.json";
        foreach (var language in SourceLanguageHelper.All)
        {
            var physical = LanguageFileMapper.ToPhysicalRelativePath(language, canonical);
            Assert.Equal(canonical, LanguageFileMapper.ToCanonicalRelativePath(physical));
        }
    }

    [Fact]
    public void 配置码_与显示名映射稳定()
    {
        Assert.Equal("ko", SourceLanguageHelper.ToCode(SourceLanguage.Korean));
        Assert.Equal("en", SourceLanguageHelper.ToCode(SourceLanguage.English));
        Assert.Equal("ja", SourceLanguageHelper.ToCode(SourceLanguage.Japanese));

        Assert.Equal(SourceLanguage.Korean, SourceLanguageHelper.TryParseCode("ko"));
        Assert.Equal(SourceLanguage.Korean, SourceLanguageHelper.TryParseCode("KR"));
        Assert.Equal(SourceLanguage.Japanese, SourceLanguageHelper.TryParseCode("jp"));
        Assert.Null(SourceLanguageHelper.TryParseCode("de"));

        Assert.Equal(SourceLanguage.English, SourceLanguageHelper.Default);
        Assert.Equal("韩文（原文）", SourceLanguageHelper.GetDisplayName(SourceLanguage.Korean));
    }

    [Fact]
    public void 三语齐全_按UnitKey对齐()
    {
        var ko = new[] { Unit("Items.json", "1", "dataList[0].name", "유료 광기") };
        var en = new[] { Unit("Items.json", "1", "dataList[0].name", "Paid Lunacy") };
        var ja = new[] { Unit("Items.json", "1", "dataList[0].name", "有償狂気") };

        var result = MultilingualAlignmentService.Align(ko, en, ja, SourceLanguage.English);

        var bundle = Assert.Single(result.Bundles);
        Assert.Equal("유료 광기", bundle.KoreanText);
        Assert.Equal("Paid Lunacy", bundle.EnglishText);
        Assert.Equal("有償狂気", bundle.JapaneseText);
        Assert.Equal("Paid Lunacy", bundle.SelectedSourceText);
        Assert.Equal(1, result.Stats.AllThree);
        Assert.Empty(bundle.Diagnostics);
    }

    [Fact]
    public void 文件顺序不同_不影响对齐结果()
    {
        var ko = new[] { Unit("A.json", "1", "f", "가"), Unit("A.json", "2", "f", "나") };
        var en = new[] { Unit("A.json", "2", "f", "B"), Unit("A.json", "1", "f", "A") };

        var result = MultilingualAlignmentService.Align(ko, en, Array.Empty<TranslationUnit>(), SourceLanguage.English);

        Assert.Equal(2, result.Bundles.Count);
        Assert.Equal("A", result.Bundles.First(b => b.Key.RecordId == "1").SelectedSourceText);
        Assert.Equal("B", result.Bundles.First(b => b.Key.RecordId == "2").SelectedSourceText);
    }

    [Fact]
    public void 缺英文_选择英文时回退韩文并记录诊断()
    {
        var ko = new[] { Unit("Items.json", "1", "dataList[0].flavor", "장미 모양") };
        var ja = new[] { Unit("Items.json", "1", "dataList[0].flavor", "薔薇型") };

        var result = MultilingualAlignmentService.Align(ko, Array.Empty<TranslationUnit>(), ja, SourceLanguage.English);

        var bundle = Assert.Single(result.Bundles);
        Assert.True(bundle.IsKoreanFallback);
        Assert.Equal("장미 모양", bundle.SelectedSourceText);
        Assert.Contains(MultilingualAlignmentService.FallbackDiagnosticCode, bundle.Diagnostics);
        Assert.Equal(1, result.Stats.FallbackToKorean);
        Assert.Equal(1, result.Stats.MissingEnglish);
    }

    [Fact]
    public void 缺日文_选择日文时回退韩文()
    {
        var ko = new[] { Unit("A.json", "1", "f", "가") };
        var en = new[] { Unit("A.json", "1", "f", "A") };

        var result = MultilingualAlignmentService.Align(ko, en, Array.Empty<TranslationUnit>(), SourceLanguage.Japanese);

        var bundle = Assert.Single(result.Bundles);
        Assert.True(bundle.IsKoreanFallback);
        Assert.Equal(1, result.Stats.MissingJapanese);
    }

    [Fact]
    public void 缺韩文_不得崩溃且记录Canonical缺失诊断()
    {
        var en = new[] { Unit("A.json", "1", "f", "A") };
        var ja = new[] { Unit("A.json", "1", "f", "あ") };

        var result = MultilingualAlignmentService.Align(Array.Empty<TranslationUnit>(), en, ja, SourceLanguage.English);

        var bundle = Assert.Single(result.Bundles);
        Assert.Null(bundle.KoreanText);
        Assert.True(bundle.CanonicalKoreanMissing);
        Assert.Contains(MultilingualAlignmentService.CanonicalMissingDiagnosticCode, bundle.Diagnostics);
        Assert.Equal(1, result.Stats.MissingKorean);
        // 该单元 EN 与 JA 都存在，因此不属于 EN-only（EnglishOnly 仅统计「只有英文」的单元）
        Assert.Equal(0, result.Stats.EnglishOnly);
    }

    [Fact]
    public void 选择韩文_使用韩文且不产生Fallback()
    {
        var ko = new[] { Unit("A.json", "1", "f", "가") };
        var en = new[] { Unit("A.json", "1", "f", "A") };
        var ja = new[] { Unit("A.json", "1", "f", "あ") };

        var result = MultilingualAlignmentService.Align(ko, en, ja, SourceLanguage.Korean);

        var bundle = Assert.Single(result.Bundles);
        Assert.Equal("가", bundle.SelectedSourceText);
        Assert.False(bundle.IsKoreanFallback);
        Assert.Equal(0, result.Stats.FallbackToKorean);
    }

    [Fact]
    public void 三语全缺_不产生绑定()
    {
        var result = MultilingualAlignmentService.Align(
            Array.Empty<TranslationUnit>(), Array.Empty<TranslationUnit>(), Array.Empty<TranslationUnit>(),
            SourceLanguage.English);

        Assert.Empty(result.Bundles);
        Assert.Equal(0, result.Stats.TotalUnits);
    }

    [Fact]
    public void 统计摘要_包含关键口径()
    {
        var ko = new[] { Unit("A.json", "1", "f", "가"), Unit("A.json", "2", "f", "나") };
        var en = new[] { Unit("A.json", "1", "f", "A") };

        var result = MultilingualAlignmentService.Align(ko, en, Array.Empty<TranslationUnit>(), SourceLanguage.English);
        var text = result.Stats.Describe();

        Assert.Contains("Canonical 单元=2", text);
        Assert.Contains("缺JP=2", text);
        Assert.Contains("Fallback→KR=1", text);
    }
}
