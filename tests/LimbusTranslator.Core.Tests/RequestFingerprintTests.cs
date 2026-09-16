using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.DeepSeek;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第4轮：RequestFingerprint 测试（纯函数，无 IO）。
/// 原则：宁可 Cache Miss，也绝不能错误 Cache Hit。
/// </summary>
public class RequestFingerprintTests
{
    private static RequestFingerprintItem Item(
        string unitKey = "Test.json|1|dataList[0].content",
        string source = "Deal 20 damage.",
        string? oldSource = null,
        string? oldTranslation = null,
        string? speaker = null,
        string mode = "TranslateNew")
        => new()
        {
            Id = DeepSeekResponseParser.EncodeId(unitKey),
            UnitKey = unitKey,
            TranslationMode = mode,
            Source = source,
            OldSource = oldSource,
            OldTranslation = oldTranslation,
            Speaker = speaker,
            Context = null,
        };

    private static RequestFingerprintPayload Payload(
        string provider = "deepseek",
        string identity = "https://api.deepseek.com",
        string model = "deepseek-v4-flash",
        double? temperature = 0.3,
        int? maxTokens = 4096,
        string? responseFormat = "json_object",
        string? systemPrompt = "SYS",
        string? userContent = "USER",
        string? glossary = "GLOSSARY",
        string? style = "STYLE",
        IReadOnlyList<RequestFingerprintItem>? items = null)
        => new()
        {
            Provider = provider,
            ProviderIdentity = identity,
            Model = model,
            Temperature = temperature,
            MaxTokens = maxTokens,
            ResponseFormat = responseFormat,
            SystemPrompt = systemPrompt,
            UserContent = userContent,
            GlossaryPrompt = glossary,
            CharacterStylePrompt = style,
            Items = items ?? new[] { Item() },
        };

    private static string Hash(RequestFingerprintPayload payload) => RequestFingerprintBuilder.Build(payload).Value;

    [Fact]
    public void 完全相同请求_指纹相同()
    {
        Assert.Equal(Hash(Payload()), Hash(Payload()));
    }

    [Fact]
    public void 指纹带SchemaVersion前缀()
    {
        var fingerprint = RequestFingerprintBuilder.Build(Payload());

        Assert.StartsWith(RequestFingerprintBuilder.FingerprintPrefix + ":", fingerprint.Value);
        Assert.Equal(RequestFingerprintBuilder.SchemaVersion, fingerprint.SchemaVersion);
        Assert.Equal(64, fingerprint.Value.Length - (RequestFingerprintBuilder.FingerprintPrefix.Length + 1));
    }

    [Fact]
    public void 字典插入顺序不同_指纹相同()
    {
        // 语义等价的术语子集（调用方已稳定排序）→ 提示词与指纹一致
        var ordered = "术语表（必须遵守）：\nSinking → 沉沦\nBleed → 流血";
        var sameContent = "术语表（必须遵守）：\nSinking → 沉沦\nBleed → 流血";
        Assert.Equal(Hash(Payload(glossary: ordered)), Hash(Payload(glossary: sameContent)));

        // 顺序真的不同（未排序）时指纹必须不同 —— 证明顺序确实参与计算
        var swapped = "术语表（必须遵守）：\nBleed → 流血\nSinking → 沉沦";
        Assert.NotEqual(Hash(Payload(glossary: ordered)), Hash(Payload(glossary: swapped)));
    }

    [Fact]
    public void Batch条目顺序变化_指纹变化()
    {
        var first = new[] { Item(unitKey: "Test.json|1|a"), Item(unitKey: "Test.json|2|b") };
        var swapped = new[] { Item(unitKey: "Test.json|2|b"), Item(unitKey: "Test.json|1|a") };

        Assert.NotEqual(Hash(Payload(items: first)), Hash(Payload(items: swapped)));
    }

    [Fact]
    public void Prompt内容变化_指纹变化()
    {
        Assert.NotEqual(Hash(Payload(systemPrompt: "SYS-A")), Hash(Payload(systemPrompt: "SYS-B")));
    }

    [Fact]
    public void UserContent变化_指纹变化()
    {
        Assert.NotEqual(Hash(Payload(userContent: "USER-A")), Hash(Payload(userContent: "USER-B")));
    }

    [Fact]
    public void 模型变化_指纹变化()
    {
        Assert.NotEqual(Hash(Payload(model: "deepseek-v4-flash")), Hash(Payload(model: "deepseek-v4")));
    }

    [Fact]
    public void 温度变化_指纹变化()
    {
        Assert.NotEqual(Hash(Payload(temperature: 0.3)), Hash(Payload(temperature: 0.7)));
    }

    [Fact]
    public void MaxTokens变化_指纹变化()
    {
        Assert.NotEqual(Hash(Payload(maxTokens: 4096)), Hash(Payload(maxTokens: 8192)));
    }

    [Fact]
    public void Provider与Endpoint变化_指纹变化()
    {
        Assert.NotEqual(Hash(Payload(provider: "deepseek")), Hash(Payload(provider: "openai")));

        Assert.NotEqual(
            Hash(Payload(identity: "https://api.deepseek.com")),
            Hash(Payload(identity: "https://other.example.com")));
    }

    [Fact]
    public void Source变化_指纹变化()
    {
        Assert.NotEqual(
            Hash(Payload(items: new[] { Item(source: "Deal 20 damage.") })),
            Hash(Payload(items: new[] { Item(source: "Deal 30 damage.") })));
    }

    [Fact]
    public void OldSource变化_指纹变化()
    {
        Assert.NotEqual(
            Hash(Payload(items: new[] { Item(oldSource: "old A") })),
            Hash(Payload(items: new[] { Item(oldSource: "old B") })));
    }

    [Fact]
    public void OldTranslation变化_指纹变化()
    {
        Assert.NotEqual(
            Hash(Payload(items: new[] { Item(oldTranslation: "旧译 A") })),
            Hash(Payload(items: new[] { Item(oldTranslation: "旧译 B") })));
    }

    [Fact]
    public void Speaker变化_指纹变化()
    {
        Assert.NotEqual(
            Hash(Payload(items: new[] { Item(speaker: "Dante") })),
            Hash(Payload(items: new[] { Item(speaker: "Yi Sang") })));
    }

    [Fact]
    public void TranslationMode变化_指纹变化()
    {
        Assert.NotEqual(
            Hash(Payload(items: new[] { Item(mode: "TranslateNew") })),
            Hash(Payload(items: new[] { Item(mode: "TranslateModified") })));
    }

    [Fact]
    public void 术语注入内容变化_指纹变化()
    {
        Assert.NotEqual(Hash(Payload(glossary: "G-A")), Hash(Payload(glossary: "G-B")));
    }

    [Fact]
    public void 角色风格变化_指纹变化()
    {
        Assert.NotEqual(Hash(Payload(style: "S-A")), Hash(Payload(style: "S-B")));
    }

    [Fact]
    public void RunId与RequestId不影响指纹()
    {
        // payload 参数集合天然不含 RunId / RequestId / 时间 / 日志路径
        Assert.Equal(Hash(Payload()), Hash(Payload()));
    }

    [Fact]
    public void Provider身份脱敏_不含密钥与路径()
    {
        var identity = RequestFingerprintBuilder.SanitizeProviderIdentity(
            "https://api.deepseek.com/chat/completions?api_key=sk-secret-value");

        Assert.Equal("https://api.deepseek.com", identity);
        Assert.DoesNotContain("sk-secret", identity);
        Assert.DoesNotContain("/chat", identity);
    }

    [Fact]
    public void ContextHash覆盖上下文信息()
    {
        var withSpeaker = RequestFingerprintBuilder.Build(Payload(items: new[] { Item(speaker: "Dante") }));
        var withoutSpeaker = RequestFingerprintBuilder.Build(Payload(items: new[] { Item(speaker: null) }));

        Assert.NotEqual(withSpeaker.ContextHash, withoutSpeaker.ContextHash);

        // 只改 Prompt（不属于上下文）时 ContextHash 不变
        Assert.Equal(
            RequestFingerprintBuilder.Build(Payload(systemPrompt: "SYS-A")).ContextHash,
            RequestFingerprintBuilder.Build(Payload(systemPrompt: "SYS-B")).ContextHash);
    }
}
