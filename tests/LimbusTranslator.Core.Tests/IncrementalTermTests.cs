using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 增量术语库组件测试（TermScanner / TermExplainer 解析）。
/// </summary>
public class IncrementalTermTests
{
    [Fact]
    public void 扫描_能提取大写专有名词()
    {
        var texts = new[]
        {
            "Blade Lineage attacks Ricardo.",
            "Blade Lineage is a faction.",
            "Ricardo fights back.",
        };

        var result = TermScanner.Scan(texts, minOccurrence: 1);

        Assert.Contains(result, kv => kv.Key == "Blade Lineage");
        Assert.Contains(result, kv => kv.Key == "Ricardo");
    }

    [Fact]
    public void 扫描_跳过已有术语库的词()
    {
        var texts = new[]
        {
            "Sinking is strong. Sinking is good.",
            "Mirror Dungeon is fun.",
        };

        // 现有术语库含 Sinking
        var glossary = new Dictionary<string, GlossaryEntry>
        {
            ["Sinking"] = new() { Translation = "沉沦", Locked = true },
        };

        var result = TermScanner.Scan(texts, glossary, minOccurrence: 1);

        Assert.DoesNotContain(result, kv => kv.Key == "Sinking");
        Assert.Contains(result, kv => kv.Key == "Mirror Dungeon");
    }

    [Fact]
    public void 扫描明细_新术语优先并过滤已收录词组的碎片()
    {
        var texts = new[]
        {
            "Blade Lineage attacks Ricardo.",
            "Ricardo joins Blade Lineage.",
        };
        var glossary = new Dictionary<string, GlossaryEntry>
        {
            ["Blade Lineage"] = new() { Translation = "剑契组", Locked = true },
        };

        var result = TermScanner.ScanDetailed(texts, glossary, minOccurrence: 1);

        var existing = Assert.Single(result, item => item.OriginalText == "Blade Lineage");
        Assert.True(existing.IsExisting);
        Assert.Equal("剑契组", existing.ExistingTranslation);
        Assert.Equal("Ricardo", result.First().OriginalText);
        Assert.DoesNotContain(result, item => item.OriginalText is "Blade" or "Lineage");
    }

    [Fact]
    public void 扫描_过滤常见英文词()
    {
        var texts = new[]
        {
            "The and for with this that from when your their after before each turn start end max base coin power level",
        };

        var result = TermScanner.Scan(texts, minOccurrence: 1);
        Assert.Empty(result);
    }

    [Fact]
    public void 严格扫描_只保留专名并去除普通词和词组碎片()
    {
        var texts = new[]
        {
            "Technology Liberation Alliance contacts Araya. Verdant Bazaar opens. Naive Faust speaks.",
            "Technology Liberation Alliance meets Araya. Verdant Bazaar closes. Naive Faust replies.",
            "Technology Liberation Alliance follows Araya. Verdant Bazaar waits. Naive Faust leaves.",
            "Free Pass Level is available. New League is open. Spiders Rooftop is closed.",
            "Free Pass Level is available. New League is open. Spiders Rooftop is closed.",
            "Fausts move.",
            "Fausts move.",
            "Fausts move.",
        };

        var result = TermScanner.ScanDetailed(
            texts,
            minOccurrence: 2,
            excludedTerms: new[] { "Faust" });

        Assert.Contains(result, item => item.OriginalText == "Technology Liberation Alliance");
        Assert.Contains(result, item => item.OriginalText == "Verdant Bazaar");
        Assert.Contains(result, item => item.OriginalText == "Araya");

        Assert.DoesNotContain(result, item => item.OriginalText is
            "Technology" or "Liberation" or "Alliance" or "Verdant" or "Bazaar"
            or "Free Pass Level" or "New League" or "Spiders Rooftop" or "Naive Faust" or "Faust" or "Fausts");
    }

    [Fact]
    public void 术语解释_解析完整()
    {
        // 直接测试 TermExplanation 的 FullExplanation 组合逻辑
        var exp = new TermExplanation
        {
            SuggestedTranslation = "里卡多",
            Meaning = "中指组织的成员",
            Origin = "源自《百年孤独》",
        };
        Assert.Contains("里卡多", exp.SuggestedTranslation);
        Assert.Contains("中指组织的成员", exp.FullExplanation);
        Assert.Contains("《百年孤独》", exp.FullExplanation);

        // 无由来时只显示含义
        var expNoOrigin = new TermExplanation
        {
            SuggestedTranslation = "无名者",
            Meaning = "一般称呼",
            Origin = "无特别由来",
        };
        Assert.DoesNotContain("【由来】", expNoOrigin.FullExplanation);
    }

    [Fact]
    public void 术语解释提示词_明确要求json对象()
    {
        Assert.True(TermExplainer.ExplainSystemPrompt.Contains("json", StringComparison.OrdinalIgnoreCase));
    }
}
