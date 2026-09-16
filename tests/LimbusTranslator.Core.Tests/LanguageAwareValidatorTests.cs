using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Validation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P1轮（主线纠偏）：**Validator 语言感知** + 新增 JAPANESE_RESIDUE /
/// CANONICAL_KOREAN_SOURCE_MISSING + 四模式术语多源匹配（Longest Match Wins）验收。
/// </summary>
public sealed class LanguageAwareValidatorTests
{
    private static DiffEntry Entry(
        string source,
        string translation,
        TranslationMode? mode = null,
        SourceLanguage? effective = null,
        string? canonicalKorean = null)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Items.json", RecordId = "1", FieldPath = "dataList[0].name" },
            NewSourceText = source,
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.TranslateNew,
            Translation = translation,
            RunTranslationMode = mode,
            EffectiveSourceLanguage = effective,
            CanonicalKoreanText = canonicalKorean,
        };

    private static ValidationReport Validate(DiffEntry entry)
        => new ValidationPipeline().ValidateAndApply(entry);

    private static ActiveGlossarySnapshot Glossary(params (string Term, string Translation)[] terms)
    {
        var entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, translation) in terms)
        {
            entries[term] = new GlossaryEntry { Translation = translation, Locked = true };
        }

        return ActiveGlossarySnapshot.FromEntries(entries, sourcePath: null);
    }

    // ───────── SOURCE_LANGUAGE_ANOMALY（语言感知） ─────────

    [Fact]
    public void SOURCE_LANGUAGE_ANOMALY_英文源文含韩文仍要告警()
    {
        // 无模式元数据 ⇒ 旧英文语义（EN_ONLY / 旧路径）
        var entry = Entry("필립 싱클레어가 탈출장치로 후퇴", "菲利普·辛克莱撤退了");
        var report = Validate(entry);

        Assert.Contains(report.Issues, issue => issue.Code == ValidationIssueCodes.SourceLanguageAnomaly);
    }

    [Fact]
    public void SOURCE_LANGUAGE_ANOMALY_KRONLY韩文原文不得告警()
    {
        // KR_ONLY：韩文就是权威原文 ⇒ 完全正常
        var entry = Entry(
            "반갑습니다", "很高兴见到您",
            TranslationMode.KoreanOnly, SourceLanguage.Korean, canonicalKorean: "반갑습니다");
        var report = Validate(entry);

        Assert.DoesNotContain(report.Issues, issue => issue.Code == ValidationIssueCodes.SourceLanguageAnomaly);
    }

    [Fact]
    public void SOURCE_LANGUAGE_ANOMALY_KR回退韩文不得告警()
    {
        // KR_EN 回退韩文：Effective = Korean ⇒ 韩文正常
        var entry = Entry(
            "반갑습니다", "很高兴见到您",
            TranslationMode.KoreanEnglish, SourceLanguage.Korean, canonicalKorean: "반갑습니다");
        var report = Validate(entry);

        Assert.DoesNotContain(report.Issues, issue => issue.Code == ValidationIssueCodes.SourceLanguageAnomaly);
    }

    [Fact]
    public void SOURCE_LANGUAGE_ANOMALY_KRJP日文源文不得告警()
    {
        var entry = Entry(
            "こんにちは、友よ", "你好，朋友",
            TranslationMode.KoreanJapanese, SourceLanguage.Japanese, canonicalKorean: "반갑습니다");
        var report = Validate(entry);

        Assert.DoesNotContain(report.Issues, issue => issue.Code == ValidationIssueCodes.SourceLanguageAnomaly);
    }

    // ───────── JAPANESE_RESIDUE（新增） ─────────

    [Fact]
    public void JAPANESE_RESIDUE_中文译文夹杂假名必须告警()
    {
        var entry = Entry("Hello", "你好です");
        var report = Validate(entry);

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.JapaneseResidue);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(nameof(JapaneseResidueValidator), issue.Validator);
    }

    [Fact]
    public void JAPANESE_RESIDUE_只按假名判定汉字不算()
    {
        // 「日本語」全是汉字、没有假名 ⇒ 不得告警（汉字是中日共用字符）
        var entry = Entry("Japanese", "日本語の翻訳".Replace("の", string.Empty));
        var report = Validate(entry);

        Assert.DoesNotContain(report.Issues, issue => issue.Code == ValidationIssueCodes.JapaneseResidue);
    }

    [Fact]
    public void JAPANESE_RESIDUE_正常中文不告警()
    {
        var entry = Entry("Hello", "你好，朋友");
        var report = Validate(entry);

        Assert.DoesNotContain(report.Issues, issue => issue.Code == ValidationIssueCodes.JapaneseResidue);
    }

    // ───────── KOREAN_RESIDUE（保持既有行为） ─────────

    [Fact]
    public void KOREAN_RESIDUE_译文残留韩文继续告警()
    {
        var entry = Entry("Hello", "안녕하세요你好");
        var report = Validate(entry);

        Assert.Contains(report.Issues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
    }

    // ───────── CANONICAL_KOREAN_SOURCE_MISSING（新增） ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public void CANONICAL_KOREAN_SOURCE_MISSING_KR模式缺韩文必须Warning且待审核(TranslationMode mode)
    {
        var effective = mode == TranslationMode.KoreanJapanese ? SourceLanguage.Japanese : SourceLanguage.English;
        var entry = Entry("Hello", "你好", mode, effective, canonicalKorean: null);   // Canonical 缺失

        var report = Validate(entry);

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.CanonicalKoreanSourceMissing);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.True(entry.NeedsReview);
    }

    [Fact]
    public void CANONICAL_KOREAN_SOURCE_MISSING_ENONLY永远不产生()
    {
        // EN_ONLY：canonical 为 N/A ⇒ 即使没有韩文也不得产生该 Issue
        var entry = Entry("Hello", "你好", TranslationMode.EnglishOnly, SourceLanguage.English);
        var report = Validate(entry);

        Assert.DoesNotContain(report.Issues, issue => issue.Code == ValidationIssueCodes.CanonicalKoreanSourceMissing);
    }

    // ───────── 四模式术语多源匹配（Longest Match Wins 继续有效） ─────────

    [Fact]
    public void 术语多源匹配_长术语优先于短术语()
    {
        var snapshot = Glossary(("Power Up", "威力提升"), ("Attack Power Up", "强壮"));

        var hits = snapshot.SelectTerms(new[] { "Attack Power Up" });

        Assert.Equal(new[] { "Attack Power Up" }, hits.Select(hit => hit.Key).ToArray());
    }

    [Fact]
    public void 术语多源匹配_跨文本取并集且稳定排序()
    {
        var snapshot = Glossary(("Sinking", "沉沦"), ("반갑습니다", "很高兴"));

        // KR_EN 的多源：EN 参考 + KR 原文 ⇒ 两个术语都应命中（跨文本并集）
        var hits = snapshot.SelectTerms(new[] { "Sinking applies.", "반갑습니다" });

        Assert.Equal(new[] { "Sinking", "반갑습니다" }, hits.Select(hit => hit.Key).ToArray());
    }
}
