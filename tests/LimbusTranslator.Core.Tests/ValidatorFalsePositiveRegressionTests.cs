using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.6轮：第8.5轮 Thinking A/B 人工审阅发现的韩文源 QA 假阳性回归用例。
/// 所有样本均为已保存 A/B 数据的离线转写；不访问 API、数据库或游戏目录。
/// </summary>
public class ValidatorFalsePositiveRegressionTests
{
    private const string PhilipSource = "필립 싱클레어가 탈출장치로 후퇴";
    private const string PhilipChinese = "菲利普辛克莱通过逃生装置撤退";

    [Theory]
    [InlineData("말풍선 특수 대사_크로머", "对话气泡特殊台词_克罗默")]
    [InlineData(PhilipSource, PhilipChinese)]
    [InlineData("가환E.G.O 침식률 n 이상", "可焕E.G.O 侵蚀率 n 以上")]
    public void 韩文源与纯中文译文_保留SourceLanguageAnomaly_不报KoreanResidue(
        string source,
        string translation)
    {
        WithPipeline(pipeline =>
        {
            var entry = Entry(source, translation);

            var report = pipeline.ValidateAndApply(entry);

            Assert.Contains(report.Issues, issue => issue.Code == ValidationIssueCodes.SourceLanguageAnomaly);
            Assert.DoesNotContain(report.Issues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
            Assert.DoesNotContain(entry.ValidationIssues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
        });
    }

    [Fact]
    public void 韩文专名连写中文Canonical_不报TerminologyMismatch()
    {
        WithPipeline(pipeline =>
        {
            var report = pipeline.ValidateAndApply(Entry(PhilipSource, PhilipChinese));

            Assert.DoesNotContain(report.Issues, issue => issue.Code == ValidationIssueCodes.TerminologyMismatch);
        });
    }

    [Theory]
    [InlineData("辛克莱通过逃生装置撤退", "필립→菲利普")]
    [InlineData("菲利普通过逃生装置撤退", "싱클레어→辛克莱")]
    public void 缺少任一LockedCanonical_只报告缺失术语(string translation, string expectedMissing)
    {
        WithPipeline(pipeline =>
        {
            var report = pipeline.ValidateAndApply(Entry(PhilipSource, translation));

            var mismatch = Assert.Single(report.Issues, issue => issue.Code == ValidationIssueCodes.TerminologyMismatch);
            Assert.Contains(expectedMissing, mismatch.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void 韩文原样返回_仍报告SourceLanguageAnomaly和KoreanResidue并需审核()
    {
        WithPipeline(pipeline =>
        {
            var entry = Entry(PhilipSource, PhilipSource);

            var report = pipeline.ValidateAndApply(entry);

            Assert.Contains(report.Issues, issue => issue.Code == ValidationIssueCodes.SourceLanguageAnomaly);
            Assert.Contains(report.Issues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
            Assert.True(entry.NeedsReview);
        });
    }

    [Fact]
    public void 同一Entry再次验证_不会继承上次KoreanResidue()
    {
        WithPipeline(pipeline =>
        {
            var entry = Entry(PhilipSource, PhilipSource);
            var first = pipeline.ValidateAndApply(entry);
            Assert.Contains(first.Issues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);

            // 模拟下一次 Provider 已给出新的纯中文结果；本轮运行状态由新结果覆盖。
            entry.Translation = PhilipChinese;
            entry.NeedsReview = false;
            entry.ReviewReason = null;
            var second = pipeline.ValidateAndApply(entry);

            Assert.DoesNotContain(second.Issues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
            Assert.DoesNotContain(entry.ValidationIssues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
            Assert.DoesNotContain(second.Issues, issue => issue.Code == ValidationIssueCodes.TerminologyMismatch);
        });
    }

    [Theory]
    [InlineData("Sinclair", "辛克莱")]
    [InlineData("싱클레어", "辛克莱")]
    [InlineData("Philip", "菲利普")]
    [InlineData("필립", "菲利普")]
    public void 默认术语表保留第85轮已锁定Alias(string source, string canonical)
    {
        Assert.True(GlossaryService.DefaultEntries.TryGetValue(source, out var entry));
        Assert.True(entry.Locked);
        Assert.Equal(canonical, entry.Translation);
    }

    private static DiffEntry Entry(string source, string translation) => new()
    {
        Key = new UnitKey { RelativeFilePath = "BattleSpeechBubbleDlg.json", RecordId = "fixture", FieldPath = "dataList[0].desc" },
        NewSourceText = source,
        DiffKind = DiffKind.Added,
        Action = TranslationAction.TranslateMissing,
        Translation = translation,
        Provenance = TranslationSource.AI,
    };

    private static void WithPipeline(Action<ValidationPipeline> assertion)
    {
        var configDir = Path.Combine(Path.GetTempPath(), "LT_VAL_FALSE_POSITIVE_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        try
        {
            File.WriteAllText(
                Path.Combine(configDir, "glossary.json"),
                """
                {
                  "Sinclair": { "translation": "辛克莱", "locked": true },
                  "싱클레어": { "translation": "辛克莱", "locked": true },
                  "Philip": { "translation": "菲利普", "locked": true },
                  "필립": { "translation": "菲利普", "locked": true }
                }
                """);

            assertion(ValidationPipeline.CreateDefault(configDir));
        }
        finally
        {
            Directory.Delete(configDir, recursive: true);
        }
    }
}
