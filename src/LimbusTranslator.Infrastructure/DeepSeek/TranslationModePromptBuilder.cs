using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.DeepSeek;

/// <summary>
/// 四模式 Prompt 区块构造（第9.0B.1轮）：把「韩文原文 / 参考译本 / 旧中文」按模式组装成提示词片段。
///
/// 关键约束：
///   - <see cref="TranslationMode.EnglishOnly"/> **绝不**出现韩文区块，也**绝不**出现「韩文权威」规则；
///   - <see cref="TranslationMode.KoreanEnglish"/> → 【韩文原文｜权威参考】+【英文译本｜主要翻译依据】；
///   - <see cref="TranslationMode.KoreanJapanese"/> → 【韩文原文｜权威参考】+【日文译本｜主要翻译依据】；
///   - <see cref="TranslationMode.KoreanOnly"/> → 只出现**一份**韩文区块（主要依据 / 权威原文）。
/// </summary>
public static class TranslationModePromptBuilder
{
    /// <summary>韩文权威规则（仅 KR_EN / KR_JP 使用）。</summary>
    public const string KoreanAuthorityRule =
        "韩文是《Limbus Company》的原始语言文本，英文 / 日文属于参考译本。\n" +
        "参考译本作为本次主要可读翻译依据；但若出现语义冲突、信息遗漏、人物称谓、人称、语气、专有名词、双关或二次翻译偏差，必须以韩文原文为最终依据。";

    /// <summary>
    /// 构造模式化源文区块。
    /// </summary>
    /// <param name="mode">翻译模式</param>
    /// <param name="korean">韩文原文（KR 模式必填）</param>
    /// <param name="reference">参考译本（EN_ONLY→英文；KR_EN→英文；KR_JP→日文；KR_ONLY→null）</param>
    /// <param name="oldKorean">旧韩文（Modified）</param>
    /// <param name="oldReference">旧参考译本（Modified）</param>
    /// <param name="oldChinese">旧中文（Modified / 继承参考）</param>
    /// <param name="isModified">是否 Modified（提供新旧对照）</param>
    public static string Build(
        TranslationMode mode,
        string? korean,
        string? reference,
        string? oldKorean = null,
        string? oldReference = null,
        string? oldChinese = null,
        bool isModified = false)
    {
        var blocks = new List<string>();

        if (mode == TranslationMode.EnglishOnly)
        {
            AddModifiedOrPlain(blocks, "英文原文", reference, oldReference, isModified, "旧英文", "新英文");
            AddOldChinese(blocks, oldChinese);
            return string.Join("\n\n", blocks);
        }

        if (mode == TranslationMode.KoreanOnly)
        {
            if (isModified)
            {
                AddOldNew(blocks, "旧韩文", oldKorean, "新韩文", korean);
            }
            else
            {
                blocks.Add("【韩文原文｜主要翻译依据 / 权威原文】\n" + (korean ?? string.Empty));
            }

            AddOldChinese(blocks, oldChinese);
            return string.Join("\n\n", blocks);
        }

        // KR_EN / KR_JP
        var referenceLabel = mode == TranslationMode.KoreanJapanese ? "日文译本｜主要翻译依据" : "英文译本｜主要翻译依据";

        if (isModified)
        {
            AddOldNew(blocks, "旧韩文", oldKorean, "新韩文", korean);
            AddOldNew(blocks, "旧" + (mode == TranslationMode.KoreanJapanese ? "日文" : "英文"),
                oldReference, "新" + (mode == TranslationMode.KoreanJapanese ? "日文" : "英文"), reference);
            AddOldChinese(blocks, oldChinese);
        }
        else
        {
            blocks.Add("【韩文原文｜权威参考】\n" + (korean ?? string.Empty));
            blocks.Add("【" + referenceLabel + "】\n" + (reference ?? string.Empty));
            AddOldChinese(blocks, oldChinese);
        }

        blocks.Add(KoreanAuthorityRule);
        return string.Join("\n\n", blocks);
    }

    private static void AddModifiedOrPlain(
        List<string> blocks, string plainLabel, string? text, string? oldText, bool isModified, string oldLabel, string newLabel)
    {
        if (isModified)
        {
            AddOldNew(blocks, oldLabel, oldText, newLabel, text);
        }
        else
        {
            blocks.Add("【" + plainLabel + "】\n" + (text ?? string.Empty));
        }
    }

    private static void AddOldNew(List<string> blocks, string oldLabel, string? oldText, string newLabel, string? newText)
    {
        blocks.Add("【" + oldLabel + "】\n" + (oldText ?? string.Empty));
        blocks.Add("【" + newLabel + "】\n" + (newText ?? string.Empty));
    }

    private static void AddOldChinese(List<string> blocks, string? oldChinese)
    {
        if (!string.IsNullOrWhiteSpace(oldChinese))
        {
            blocks.Add("【旧中文｜仅用于继承未变化部分】\n" + oldChinese);
        }
    }
}