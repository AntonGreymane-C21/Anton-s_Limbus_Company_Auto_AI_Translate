using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B.2轮：四模式**实际请求体**层面的接线验证（`BuildRequestBodyJson`）。
/// 断言的是真实发送给模型的 JSON（system 内容 + items），而不是仅 PromptBuilder 的输出。
/// </summary>
public sealed class FourModeRequestBodyTests
{
    private static readonly DeepSeekOptions Options = new()
    {
        ApiKey = "test-key",
        ApiUrl = "https://api.deepseek.com/chat/completions",
        Model = "deepseek-v4-flash",
    };

    private static DeepSeekTranslateRequestItem Item(string source, string? korean, string? oldKorean = null)
        => new()
        {
            Id = "Items.json|1|dataList[0].name",
            Source = source,
            CanonicalKorean = korean,
            OldCanonicalKorean = oldKorean,
        };

    private static string Body(TranslationMode mode, params DeepSeekTranslateRequestItem[] items)
        => DeepSeekRequestComposer.BuildRequestBodyJson(
            Options,
            new PromptOptions(),
            "B1",
            items,
            glossaryPrompt: string.Empty,
            characterStylePrompt: string.Empty,
            thinking: new DeepSeekRequestThinking(false, null),
            includeModifiedRule: false,
            category: null,
            mode: mode);

    private static string SystemContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
    }

    private static string UserContent(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString() ?? string.Empty;
    }

    /// <summary>解析 user content 内部 JSON，读取首个 item 的指定字段（缺失返回 null）。</summary>
    private static string? UserItemField(string body, string field)
    {
        using var doc = JsonDocument.Parse(UserContent(body));
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0)
        {
            return null;
        }

        return items[0].TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    [Fact]
    public void ENONLY请求体_不含韩文与权威规则()
    {
        // EN_ONLY：管线不会注入韩文（CanonicalKorean=null），因此请求体不得出现韩文
        var body = Body(TranslationMode.EnglishOnly, Item("Paid Lunacy", korean: null));

        Assert.Equal("Paid Lunacy", UserItemField(body, "Source"));
        Assert.Null(UserItemField(body, "CanonicalKorean"));                    // EN_ONLY 不发送韩文
        Assert.DoesNotContain("原始语言文本", SystemContent(body));              // 权威规则不得出现
    }

    [Fact]
    public void KREN请求体_包含韩文与权威规则()
    {
        var body = Body(TranslationMode.KoreanEnglish, Item("Paid Lunacy", korean: "유료 광기"));

        var system = SystemContent(body);

        Assert.Equal("유료 광기", UserItemField(body, "CanonicalKorean"));       // 韩文以独立字段进入请求
        Assert.Contains(TranslationModePromptBuilder.KoreanAuthorityRule, system);
    }

    [Fact]
    public void KRJP请求体_同样包含韩文与权威规则()
    {
        var body = Body(TranslationMode.KoreanJapanese, Item("有償狂気", korean: "유료 광기"));

        Assert.Equal("유료 광기", UserItemField(body, "CanonicalKorean"));
        Assert.Contains(TranslationModePromptBuilder.KoreanAuthorityRule, SystemContent(body));
    }

    [Fact]
    public void KRONLY请求体_只出现一份韩文且无译本必需()
    {
        var body = Body(TranslationMode.KoreanOnly, Item("유료 광기", korean: "유료 광기"));

        // KR_ONLY：Source 本身即韩文；韩文只出现一份（Source），不额外注入 CanonicalKorean 之外的副本
        Assert.Equal("유료 광기", UserItemField(body, "Source"));
        Assert.Equal("유료 광기", UserItemField(body, "CanonicalKorean"));
        Assert.DoesNotContain(TranslationModePromptBuilder.KoreanAuthorityRule, SystemContent(body));
    }

    [Fact]
    public void Modified请求体_包含旧新韩文()
    {
        var body = Body(
            TranslationMode.KoreanEnglish,
            Item("Paid Lunacy", korean: "유료 광기", oldKorean: "무료 광기"));

        Assert.Equal("무료 광기", UserItemField(body, "OldCanonicalKorean"));
        Assert.Equal("유료 광기", UserItemField(body, "CanonicalKorean"));
    }

    [Fact]
    public void ENONLY与KREN请求体必须不同_即使英文源文相同()
    {
        var enOnly = Body(TranslationMode.EnglishOnly, Item("Paid Lunacy", korean: null));
        var krEn = Body(TranslationMode.KoreanEnglish, Item("Paid Lunacy", korean: "유료 광기"));

        Assert.NotEqual(enOnly, krEn);
    }
}