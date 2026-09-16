namespace LimbusTranslator.Core.Models;

/// <summary>
/// 校验问题稳定机器码。
/// 第3轮 ReleaseGate 与 UI 一律按 Code / Severity / Category 处理，禁止匹配自然语言措辞。
/// </summary>
public static class ValidationIssueCodes
{
    /// <summary>源文非空但译文为空</summary>
    public const string EmptyTranslation = "EMPTY_TRANSLATION";

    /// <summary>译文与源文完全相同（明显应该翻译的文本）</summary>
    public const string SameAsSource = "SAME_AS_SOURCE";

    /// <summary>占位符缺失 / 重复 / 未恢复</summary>
    public const string PlaceholderMismatch = "PLACEHOLDER_MISMATCH";

    /// <summary>富文本标签结构不一致</summary>
    public const string TagMismatch = "TAG_MISMATCH";

    /// <summary>可见数字集合不一致</summary>
    public const string NumberMismatch = "NUMBER_MISMATCH";

    /// <summary>换行数量不一致</summary>
    public const string LineBreakMismatch = "LINEBREAK_MISMATCH";

    /// <summary>明显英文残留</summary>
    public const string EnglishResidue = "ENGLISH_RESIDUE";

    /// <summary>韩文残留</summary>
    public const string KoreanResidue = "KOREAN_RESIDUE";

    /// <summary>长度极端异常</summary>
    public const string LengthAnomaly = "LENGTH_ANOMALY";

    /// <summary>命中 locked 术语但未使用规定译法</summary>
    public const string TerminologyMismatch = "TERMINOLOGY_MISMATCH";

    /// <summary>
    /// 源语言异常（第8.5轮）：SourceText 本应为英文，却出现明显 Hangul（韩文）。
    /// 检查的是**源文**而不是译文；Warning 级，不进入 HardSafety Block。
    ///
    /// 第9.0B-P1轮：**语言感知** —— 只有「生效源语言 = 英文」时才可能触发；
    /// KR_ONLY（源文就是韩文）与 KR 模式回退韩文时不得告警。
    /// </summary>
    public const string SourceLanguageAnomaly = "SOURCE_LANGUAGE_ANOMALY";

    /// <summary>
    /// 日文残留（第9.0B-P1轮）：最终中文译文里出现平假名 / 片假名。
    /// 只按假名 Unicode 判定，**不用汉字判断**（汉字是中日共用的）；Warning 级。
    /// </summary>
    public const string JapaneseResidue = "JAPANESE_RESIDUE";

    /// <summary>
    /// Canonical 韩文原文缺失（第9.0B-P1轮）：仅对 KR_EN / KR_JP / KR_ONLY 产生。
    /// EN_ONLY **永远**不产生此 Issue。
    /// </summary>
    public const string CanonicalKoreanSourceMissing = "CANONICAL_KOREAN_SOURCE_MISSING";

    /// <summary>校验器自身执行失败（安全网，正常不应出现）</summary>
    public const string ValidatorFailure = "VALIDATOR_FAILURE";
}
