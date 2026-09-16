using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Translation;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.75轮：自适应 Thinking 策略测试。
///
/// 正式策略（优先级）：SOURCE_LANGUAGE_ANOMALY &gt; StoryData &gt; 普通英文。
/// </summary>
public class TranslationThinkingPolicyTests
{
    private static DiffEntry Entry(string file, string fieldPath, string source)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = "1", FieldPath = fieldPath },
            NewSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };

    private static readonly TranslationThinkingPolicy Adaptive = new();

    [Theory]
    [InlineData("Skills.json", "dataList[0].levelList[0].name")]
    [InlineData("Items.json", "dataList[0].name")]
    [InlineData("Bufs.json", "dataList[0].desc")]
    public void 普通英文_决策为OFF(string file, string fieldPath)
    {
        var decision = Adaptive.Decide(Entry(file, fieldPath, "Inflict 2 Sinking."));

        Assert.False(decision.Enabled);
        Assert.Equal(ThinkingPolicyReasons.DefaultOff, decision.Reason);
    }

    [Theory]
    [InlineData("StoryData/3D309I.json", "dataList[0].content")]
    [InlineData("StoryData/3D309I.json", "dataList[0].title")]
    public void StoryData英文_决策为ON(string file, string fieldPath)
    {
        var decision = Adaptive.Decide(Entry(file, fieldPath, "The Sinners march on."));

        Assert.True(decision.Enabled);
        Assert.Equal(ThinkingPolicyReasons.StoryData, decision.Reason);
    }

    [Fact]
    public void 韩文非StoryData_决策为ON()
    {
        var decision = Adaptive.Decide(Entry("BattleSpeechBubbleDlg.json", "dataList[0].desc", "말풍선 특수 대사_크로머"));

        Assert.True(decision.Enabled);
        Assert.Equal(ThinkingPolicyReasons.SourceLanguageAnomaly, decision.Reason);
    }

    [Fact]
    public void 韩文StoryData_优先级高于StoryData且决策为ON()
    {
        var decision = Adaptive.Decide(Entry("StoryData/S949A.json", "dataList[3].content", "필립 싱클레어가 탈출장치로 후퇴"));

        Assert.True(decision.Enabled);
        Assert.Equal(ThinkingPolicyReasons.SourceLanguageAnomaly, decision.Reason);
    }

    [Fact]
    public void 英文中夹韩文_决策为ON()
    {
        var decision = Adaptive.Decide(Entry("Skills.json", "dataList[0].name", "Use 필립 to open the door."));

        Assert.True(decision.Enabled);
        Assert.Equal(ThinkingPolicyReasons.SourceLanguageAnomaly, decision.Reason);
    }

    [Fact]
    public void 强制ON_覆盖所有来源()
    {
        var policy = new TranslationThinkingPolicy(TranslationThinkingMode.AlwaysOn, "low");

        var plain = policy.Decide(Entry("Items.json", "dataList[0].name", "Plain english."));

        Assert.True(plain.Enabled);
        Assert.Equal(ThinkingPolicyReasons.AlwaysOn, plain.Reason);
        Assert.Equal("low", plain.ReasoningEffort);
    }

    [Fact]
    public void 强制OFF_覆盖所有来源()
    {
        var policy = new TranslationThinkingPolicy(TranslationThinkingMode.AlwaysOff);

        var korean = policy.Decide(Entry("StoryData/S949A.json", "dataList[1].content", "필립 싱클레어"));

        Assert.False(korean.Enabled);
        Assert.Equal(ThinkingPolicyReasons.AlwaysOff, korean.Reason);
        Assert.Null(korean.ReasoningEffort);
    }

    [Fact]
    public void 相同输入_决策与原因稳定一致()
    {
        var entry = Entry("StoryData/S949A.json", "dataList[2].content", "필립 싱클레어가 탈출장치로 후퇴");

        var first = Adaptive.Decide(entry);
        var second = Adaptive.Decide(entry);

        Assert.Equal(first, second);
    }

    // ---------------- FromOptions 解析规则 ----------------

    [Fact]
    public void 配置未设置任何Thinking字段_使用Adaptive()
    {
        var policy = TranslationThinkingPolicy.FromOptions(new DeepSeekOptions());

        Assert.Equal(TranslationThinkingMode.Adaptive, policy.Mode);
        Assert.False(policy.Decide(Entry("Items.json", "dataList[0].name", "Plain.")).Enabled);
    }

    [Fact]
    public void 旧配置thinking为true_映射为AlwaysOn()
    {
        var policy = TranslationThinkingPolicy.FromOptions(new DeepSeekOptions { Thinking = true });

        Assert.Equal(TranslationThinkingMode.AlwaysOn, policy.Mode);
        Assert.True(policy.Decide(Entry("Items.json", "dataList[0].name", "Plain.")).Enabled);
    }

    [Fact]
    public void 旧配置thinking为false_映射为AlwaysOff()
    {
        var policy = TranslationThinkingPolicy.FromOptions(new DeepSeekOptions
        {
            Thinking = false,
            ThinkingMode = TranslationThinkingMode.AlwaysOff,
        });

        Assert.Equal(TranslationThinkingMode.AlwaysOff, policy.Mode);
        Assert.False(policy.Decide(Entry("StoryData/S949A.json", "dataList[1].content", "필립")).Enabled);
    }

    [Fact]
    public void 显式thinkingMode优先于旧thinking字段()
    {
        var policy = TranslationThinkingPolicy.FromOptions(new DeepSeekOptions
        {
            Thinking = true,
            ThinkingMode = TranslationThinkingMode.Adaptive,
        });

        Assert.Equal(TranslationThinkingMode.Adaptive, policy.Mode);
        Assert.False(policy.Decide(Entry("Items.json", "dataList[0].name", "Plain english.")).Enabled);
    }

    [Fact]
    public void 含韩文的多种文字都判定为源语言异常()
    {
        Assert.True(LimbusTranslator.Core.Text.SourceLanguageDetector.ContainsHangul("한국어"));
        Assert.True(LimbusTranslator.Core.Text.SourceLanguageDetector.ContainsHangul("mix 필립 text"));
        Assert.False(LimbusTranslator.Core.Text.SourceLanguageDetector.ContainsHangul("English only."));
        Assert.Equal(2, LimbusTranslator.Core.Text.SourceLanguageDetector.CountHangul("가나"));
    }
}
