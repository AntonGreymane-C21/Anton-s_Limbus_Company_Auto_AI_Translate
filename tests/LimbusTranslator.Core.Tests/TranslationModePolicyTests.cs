using LimbusTranslator.Core.Models;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0B.1轮：四模式策略与 TM 盐隔离。</summary>
public sealed class TranslationModePolicyTests
{
    [Fact]
    public void 只有三个KR模式使用CanonicalDiff()
    {
        Assert.False(TranslationModePolicy.UsesCanonicalKoreanDiff(TranslationMode.EnglishOnly));
        Assert.True(TranslationModePolicy.UsesCanonicalKoreanDiff(TranslationMode.KoreanEnglish));
        Assert.True(TranslationModePolicy.UsesCanonicalKoreanDiff(TranslationMode.KoreanJapanese));
        Assert.True(TranslationModePolicy.UsesCanonicalKoreanDiff(TranslationMode.KoreanOnly));

        Assert.False(TranslationModePolicy.UsesBaselineMigration(TranslationMode.EnglishOnly));
        Assert.True(TranslationModePolicy.UsesBaselineMigration(TranslationMode.KoreanEnglish));
    }

    [Fact]
    public void ENONLY绝不发送韩文也不含权威规则()
    {
        Assert.False(TranslationModePolicy.SendsKorean(TranslationMode.EnglishOnly));
        Assert.False(TranslationModePolicy.IncludesKoreanAuthorityRule(TranslationMode.EnglishOnly));
        Assert.False(TranslationModePolicy.AppliesCanonicalMissingWarning(TranslationMode.EnglishOnly));
        Assert.False(TranslationModePolicy.AllowsKoreanFallback(TranslationMode.EnglishOnly));
        Assert.True(TranslationModePolicy.CanonicalFieldsNotApplicable(TranslationMode.EnglishOnly));
    }

    [Fact]
    public void 权威规则仅在KREN与KRJP()
    {
        Assert.True(TranslationModePolicy.IncludesKoreanAuthorityRule(TranslationMode.KoreanEnglish));
        Assert.True(TranslationModePolicy.IncludesKoreanAuthorityRule(TranslationMode.KoreanJapanese));
        Assert.False(TranslationModePolicy.IncludesKoreanAuthorityRule(TranslationMode.KoreanOnly));
    }

    [Fact]
    public void 参考译本语言按模式确定()
    {
        Assert.Equal(SourceLanguage.English, TranslationModePolicy.GetReferenceLanguage(TranslationMode.EnglishOnly));
        Assert.Equal(SourceLanguage.English, TranslationModePolicy.GetReferenceLanguage(TranslationMode.KoreanEnglish));
        Assert.Equal(SourceLanguage.Japanese, TranslationModePolicy.GetReferenceLanguage(TranslationMode.KoreanJapanese));
        Assert.Null(TranslationModePolicy.GetReferenceLanguage(TranslationMode.KoreanOnly));
    }

    [Fact]
    public void 术语匹配源按模式确定()
    {
        Assert.Equal(new[] { SourceLanguage.English }, TranslationModePolicy.GetGlossarySources(TranslationMode.EnglishOnly));
        Assert.Equal(new[] { SourceLanguage.Korean, SourceLanguage.English }, TranslationModePolicy.GetGlossarySources(TranslationMode.KoreanEnglish));
        Assert.Equal(new[] { SourceLanguage.Korean, SourceLanguage.Japanese }, TranslationModePolicy.GetGlossarySources(TranslationMode.KoreanJapanese));
        Assert.Equal(new[] { SourceLanguage.Korean }, TranslationModePolicy.GetGlossarySources(TranslationMode.KoreanOnly));
    }

    [Fact]
    public void KRONLY缺韩文不得调用Provider且强制思考()
    {
        Assert.True(TranslationModePolicy.RequiresKoreanSource(TranslationMode.KoreanOnly));
        Assert.False(TranslationModePolicy.RequiresKoreanSource(TranslationMode.KoreanEnglish));
        Assert.True(TranslationModePolicy.ShouldForceThinking(TranslationMode.KoreanOnly, SourceLanguage.Korean));
        Assert.True(TranslationModePolicy.ShouldForceThinking(TranslationMode.KoreanEnglish, SourceLanguage.Korean));
        Assert.False(TranslationModePolicy.ShouldForceThinking(TranslationMode.EnglishOnly, SourceLanguage.English));
    }

    [Fact]
    public void ENONLY盐为null以兼容旧TM()
    {
        Assert.Null(TranslationModePolicy.BuildModeSalt(TranslationMode.EnglishOnly, SourceLanguage.English, null));
        Assert.Null(TranslationModePolicy.BuildModeSalt(TranslationMode.EnglishOnly, SourceLanguage.English, "무관"));
    }

    [Fact]
    public void 四模式盐两两不同_即使文本相同()
    {
        var salts = TranslationModeCodes.All
            .Where(m => m != TranslationMode.EnglishOnly)
            .Select(m => TranslationModePolicy.BuildModeSalt(m, SourceLanguage.Korean, "가"))
            .ToList();

        Assert.Equal(3, salts.Distinct().Count());
        Assert.DoesNotContain(null, salts);
    }

    [Fact]
    public void Fallback场景仍按模式隔离()
    {
        var krEn = TranslationModePolicy.BuildModeSalt(TranslationMode.KoreanEnglish, SourceLanguage.Korean, "가");
        var krJp = TranslationModePolicy.BuildModeSalt(TranslationMode.KoreanJapanese, SourceLanguage.Korean, "가");
        var krOnly = TranslationModePolicy.BuildModeSalt(TranslationMode.KoreanOnly, SourceLanguage.Korean, "가");

        Assert.NotEqual(krEn, krJp);
        Assert.NotEqual(krEn, krOnly);
        Assert.NotEqual(krJp, krOnly);
    }
}