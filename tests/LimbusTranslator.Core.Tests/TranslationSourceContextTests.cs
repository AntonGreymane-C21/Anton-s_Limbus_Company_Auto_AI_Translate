using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Multilingual;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0B轮：翻译输入上下文（Requested / Effective 分离、Fallback、Canonical 缺失）。</summary>
public sealed class TranslationSourceContextTests
{
    private static UnitKey Key() => new()
    {
        RelativeFilePath = "Items.json",
        RecordId = "1",
        FieldPath = "dataList[0].name",
    };

    [Fact]
    public void EN模式_生效语言为英文()
    {
        var ctx = TranslationSourceContextBuilder.Build(Key(), SourceLanguage.English, "가", "A", "あ");

        Assert.Equal(SourceLanguage.English, ctx.RequestedLanguage);
        Assert.Equal(SourceLanguage.English, ctx.EffectiveLanguage);
        Assert.Equal("A", ctx.SelectedSourceText);
        Assert.Equal("가", ctx.CanonicalKoreanText);
        Assert.False(ctx.IsKoreanFallback);
        Assert.False(ctx.KoreanIsPrimary);
    }

    [Fact]
    public void JP模式_生效语言为日文()
    {
        var ctx = TranslationSourceContextBuilder.Build(Key(), SourceLanguage.Japanese, "가", "A", "あ");

        Assert.Equal(SourceLanguage.Japanese, ctx.EffectiveLanguage);
        Assert.Equal("あ", ctx.SelectedSourceText);
        Assert.False(ctx.IsKoreanFallback);
    }

    [Fact]
    public void KO模式_韩文即主依据()
    {
        var ctx = TranslationSourceContextBuilder.Build(Key(), SourceLanguage.Korean, "가", "A", "あ");

        Assert.True(ctx.KoreanIsPrimary);
        Assert.Equal("가", ctx.SelectedSourceText);
        Assert.Equal(SourceLanguage.Korean, ctx.EffectiveLanguage);
    }

    [Fact]
    public void EN缺失_Requested与Effective必须分离()
    {
        var ctx = TranslationSourceContextBuilder.Build(Key(), SourceLanguage.English, "가", null, "あ");

        Assert.Equal(SourceLanguage.English, ctx.RequestedLanguage);
        Assert.Equal(SourceLanguage.Korean, ctx.EffectiveLanguage);
        Assert.True(ctx.IsKoreanFallback);
        Assert.True(ctx.UsedFallback);
        Assert.Equal("가", ctx.SelectedSourceText);
    }

    [Fact]
    public void 日文缺失_选择日文时回退韩文()
    {
        var ctx = TranslationSourceContextBuilder.Build(Key(), SourceLanguage.Japanese, "가", "A", null);

        Assert.Equal(SourceLanguage.Japanese, ctx.RequestedLanguage);
        Assert.Equal(SourceLanguage.Korean, ctx.EffectiveLanguage);
        Assert.True(ctx.IsKoreanFallback);
    }

    [Fact]
    public void 韩文缺失_标记CanonicalMissing且不伪造()
    {
        var ctx = TranslationSourceContextBuilder.Build(Key(), SourceLanguage.English, null, "A", null);

        Assert.True(ctx.CanonicalKoreanMissing);
        Assert.Null(ctx.CanonicalKoreanText);
        Assert.Equal("A", ctx.SelectedSourceText);
        Assert.False(ctx.IsKoreanFallback);
    }

    [Fact]
    public void 变化标记_来自新旧文本比较()
    {
        var ctx = TranslationSourceContextBuilder.Build(
            Key(), SourceLanguage.English, "나", "A2", "あ",
            oldKoreanText: "가", oldEnglishText: "A", oldJapaneseText: "あ", oldChinese: "旧中文");

        Assert.True(ctx.CanonicalChanged);
        Assert.True(ctx.EnglishChanged);
        Assert.False(ctx.JapaneseChanged);
        Assert.Equal("旧中文", ctx.OldChinese);
        Assert.Equal("가", ctx.OldCanonicalKoreanText);
        Assert.Equal("A", ctx.OldSelectedSourceText);
    }
}