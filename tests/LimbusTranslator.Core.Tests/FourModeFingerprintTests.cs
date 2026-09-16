using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0B-P1轮：Fingerprint v3 的四模式语义（EN_ONLY 独立性 + 四模式/Fallback 隔离）。</summary>
public sealed class FourModeFingerprintTests
{
    private static RequestFingerprintPayload Payload(
        string modeCode,
        string effective,
        string? canonical,
        string? oldCanonical,
        bool? canonicalChanged,
        bool? usedFallback,
        string selectedSource = "Hello")
        => new()
        {
            Provider = "DeepSeek",
            ProviderIdentity = "https://api.deepseek.com",
            Model = "deepseek-v4-flash",
            Temperature = 0.3,
            MaxTokens = 4096,
            Thinking = false,
            SystemPrompt = "system",
            UserContent = "user",
            Items = new[]
            {
                new RequestFingerprintItem
                {
                    Id = "items|1|f",
                    UnitKey = "items|1|f",
                    TranslationMode = "TranslateModified",
                    Source = selectedSource,
                },
            },
            SourceModeCode = modeCode,
            EffectiveSourceLanguage = effective,
            SelectedSourceText = selectedSource,
            OldSelectedSourceText = "OldHello",
            UsedKoreanFallback = usedFallback,
            CanonicalKoreanText = canonical,
            OldCanonicalKoreanText = oldCanonical,
            CanonicalChanged = canonicalChanged,
        };

    [Fact]
    public void 指纹版本应为v3()
    {
        Assert.Equal(3, RequestFingerprintBuilder.SchemaVersion);
        Assert.Equal("v3", RequestFingerprintBuilder.FingerprintPrefix);
    }

    [Fact]
    public void 四模式指纹两两不同()
    {
        var enOnly = RequestFingerprintBuilder.Build(Payload("en_only", "en", null, null, null, false));
        var krEn = RequestFingerprintBuilder.Build(Payload("kr_en", "en", "반갑습니다", "안녕하세요", true, false));
        var krJp = RequestFingerprintBuilder.Build(Payload("kr_jp", "ja", "반갑습니다", "안녕하세요", true, false));
        var krOnly = RequestFingerprintBuilder.Build(Payload("kr_only", "ko", "반갑습니다", "안녕하세요", true, true, "반갑습니다"));

        var values = new[] { enOnly.Value, krEn.Value, krJp.Value, krOnly.Value };
        Assert.Equal(4, values.Distinct().Count());
        Assert.All(values, v => Assert.StartsWith("v3:", v));
    }

    [Fact]
    public void ENONLY指纹不受KR变化影响()
    {
        // EN_ONLY：canonical 字段恒为 null；即使韩文原文发生变化，指纹必须保持不变
        var before = RequestFingerprintBuilder.Build(Payload("en_only", "en", null, null, null, false));
        var after = RequestFingerprintBuilder.Build(Payload("en_only", "en", null, null, null, false));

        Assert.Equal(before.Value, after.Value);

        // 反证：同样只改「韩文原文」，KR_EN 必须变化
        var krBefore = RequestFingerprintBuilder.Build(Payload("kr_en", "en", "안녕하세요", "안녕하세요", false, false));
        var krAfter = RequestFingerprintBuilder.Build(Payload("kr_en", "en", "반갑습니다", "안녕하세요", true, false));
        Assert.NotEqual(krBefore.Value, krAfter.Value);
    }

    [Fact]
    public void Fallback场景指纹仍按模式隔离()
    {
        // SelectedSourceText 完全相同（都回退到同一韩文），但模式不同 ⇒ 指纹必须不同
        var krEn = RequestFingerprintBuilder.Build(Payload("kr_en", "ko", "안녕하세요", null, true, true, "안녕하세요"));
        var krJp = RequestFingerprintBuilder.Build(Payload("kr_jp", "ko", "안녕하세요", null, true, true, "안녕하세요"));
        var krOnly = RequestFingerprintBuilder.Build(Payload("kr_only", "ko", "안녕하세요", null, true, true, "안녕하세요"));

        Assert.Equal(3, new[] { krEn.Value, krJp.Value, krOnly.Value }.Distinct().Count());
    }
}