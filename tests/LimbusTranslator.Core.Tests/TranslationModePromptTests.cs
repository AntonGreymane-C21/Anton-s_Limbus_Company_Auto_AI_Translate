using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.DeepSeek;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0B.1轮：四模式 Prompt 区块（含 EN_ONLY 不得出现韩文的关键回归）。</summary>
public sealed class TranslationModePromptTests
{
    [Fact]
    public void ENONLY提示词只有英文_绝不出现韩文与权威规则()
    {
        var prompt = TranslationModePromptBuilder.Build(
            TranslationMode.EnglishOnly, korean: "가", reference: "A");

        Assert.Contains("【英文原文】", prompt);
        Assert.Contains("A", prompt);
        Assert.DoesNotContain("가", prompt);
        Assert.DoesNotContain("韩文", prompt);           // §43 关键回归：EN_ONLY 不得出现韩文权威参考
        Assert.DoesNotContain(TranslationModePromptBuilder.KoreanAuthorityRule, prompt);
    }

    [Fact]
    public void KREN提示词包含韩文与英文及权威规则()
    {
        var prompt = TranslationModePromptBuilder.Build(
            TranslationMode.KoreanEnglish, korean: "가", reference: "A");

        Assert.Contains("【韩文原文｜权威参考】", prompt);
        Assert.Contains("가", prompt);
        Assert.Contains("【英文译本｜主要翻译依据】", prompt);
        Assert.Contains("A", prompt);
        Assert.Contains(TranslationModePromptBuilder.KoreanAuthorityRule, prompt);
    }

    [Fact]
    public void KRJP提示词包含韩文与日文()
    {
        var prompt = TranslationModePromptBuilder.Build(
            TranslationMode.KoreanJapanese, korean: "가", reference: "あ");

        Assert.Contains("【韩文原文｜权威参考】", prompt);
        Assert.Contains("【日文译本｜主要翻译依据】", prompt);
        Assert.Contains("あ", prompt);
        Assert.Contains(TranslationModePromptBuilder.KoreanAuthorityRule, prompt);
    }

    [Fact]
    public void KRONLY只出现一份韩文()
    {
        var prompt = TranslationModePromptBuilder.Build(
            TranslationMode.KoreanOnly, korean: "가", reference: null);

        Assert.Contains("【韩文原文｜主要翻译依据 / 权威原文】", prompt);
        Assert.Equal(1, CountOccurrences(prompt, "가"));
        Assert.DoesNotContain("译本｜主要翻译依据", prompt);
    }

    [Fact]
    public void Modified_ENONLY提供新旧英文与旧中文()
    {
        var prompt = TranslationModePromptBuilder.Build(
            TranslationMode.EnglishOnly, korean: null, reference: "A2",
            oldKorean: null, oldReference: "A1", oldChinese: "旧中文", isModified: true);

        Assert.Contains("【旧英文】", prompt);
        Assert.Contains("A1", prompt);
        Assert.Contains("【新英文】", prompt);
        Assert.Contains("A2", prompt);
        Assert.Contains("旧中文", prompt);
        Assert.DoesNotContain("韩文", prompt);
    }

    [Fact]
    public void Modified_KREN提供五元组且强调新韩文优先()
    {
        var prompt = TranslationModePromptBuilder.Build(
            TranslationMode.KoreanEnglish, korean: "나", reference: "C",
            oldKorean: "가", oldReference: "C", oldChinese: "旧中文", isModified: true);

        Assert.Contains("【旧韩文】", prompt);
        Assert.Contains("가", prompt);
        Assert.Contains("【新韩文】", prompt);
        Assert.Contains("나", prompt);
        Assert.Contains("【旧英文】", prompt);
        Assert.Contains("【新英文】", prompt);
        Assert.Contains("旧中文", prompt);
        Assert.Contains("必须以韩文原文为最终依据", prompt);
    }

    [Fact]
    public void Modified_KRJP提供旧新韩文与旧新日文()
    {
        var prompt = TranslationModePromptBuilder.Build(
            TranslationMode.KoreanJapanese, korean: "나", reference: "い",
            oldKorean: "가", oldReference: "あ", oldChinese: null, isModified: true);

        Assert.Contains("【旧韩文】", prompt);
        Assert.Contains("【新韩文】", prompt);
        Assert.Contains("【旧日文】", prompt);
        Assert.Contains("【新日文】", prompt);
    }

    [Fact]
    public void Modified_KRONLY只有旧新韩文与旧中文()
    {
        var prompt = TranslationModePromptBuilder.Build(
            TranslationMode.KoreanOnly, korean: "나", reference: null,
            oldKorean: "가", oldReference: null, oldChinese: "旧中文", isModified: true);

        Assert.Contains("【旧韩文】", prompt);
        Assert.Contains("【新韩文】", prompt);
        Assert.Contains("旧中文", prompt);
        Assert.DoesNotContain("英文", prompt);
        Assert.DoesNotContain("日文", prompt);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }
}