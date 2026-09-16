using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第2轮：单个 Validator 与 ValidationPipeline 的规则测试（纯函数、无副作用）。
/// </summary>
public class ValidatorPipelineTests
{
    private static UnitKey Key(string fieldPath = "dataList[0].content")
        => new() { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = fieldPath };

    private static ValidationContext Context(
        string source,
        string translation,
        TranslationSource? provenance = null,
        IReadOnlyList<TerminologyRequirement>? terminology = null,
        IReadOnlySet<string>? allowed = null)
        => new()
        {
            Key = Key(),
            SourceText = source,
            Translation = translation,
            Provenance = provenance,
            Terminology = terminology ?? Array.Empty<TerminologyRequirement>(),
            AllowedEnglishTerms = allowed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };

    private static ValidationPipeline Pipeline(ValidationOptions? options = null)
        => new(null, null, options ?? ValidationOptions.Default);

    // ---------- Empty ----------

    [Fact]
    public void Source非空_Target为空_报EmptyError()
    {
        var report = Pipeline().Validate(Context("Real English text.", string.Empty));

        var issue = Assert.Single(report.Issues);
        Assert.Equal(ValidationIssueCodes.EmptyTranslation, issue.Code);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal(ValidationCategory.Structure, issue.Category);
        Assert.True(report.HasError);
    }

    [Fact]
    public void Source空_Target空_不报EmptyError()
    {
        var report = Pipeline().Validate(Context(string.Empty, string.Empty));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.EmptyTranslation);
        Assert.False(report.HasError);
    }

    [Fact]
    public void Source空_Target非空_不报EmptyError()
    {
        var report = Pipeline().Validate(Context(string.Empty, "既有旧汉化内容"));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.EmptyTranslation);
    }

    [Fact]
    public void Source纯空白_Target空_不报EmptyError()
    {
        var report = Pipeline().Validate(Context("   ", string.Empty));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.EmptyTranslation);
    }

    // ---------- SameAsSource ----------

    [Fact]
    public void 空文本_SameAsSource跳过()
    {
        Assert.DoesNotContain(
            Pipeline().Validate(Context(string.Empty, string.Empty)).Issues,
            i => i.Code == ValidationIssueCodes.SameAsSource);
        Assert.DoesNotContain(
            Pipeline().Validate(Context("Deal 20 damage to the enemy.", string.Empty)).Issues,
            i => i.Code == ValidationIssueCodes.SameAsSource);
    }

    [Fact]
    public void 完整英文与源文相同_报SameAsSource()
    {
        const string text = "Deal 20 damage to the enemy and gain 2 Haste.";
        var report = Pipeline().Validate(Context(text, text));

        Assert.Contains(report.Issues, i =>
            i.Code == ValidationIssueCodes.SameAsSource && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public void 短缩写与源文相同_不报SameAsSource()
    {
        foreach (var text in new[] { "HP", "SP", "E.G.O", "ID", "UI", "20", "100%", "---", "<b>HP</b>" })
        {
            var report = Pipeline().Validate(Context(text, text));
            Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.SameAsSource);
        }
    }

    // ---------- Placeholder ----------

    [Fact]
    public void Placeholder丢失_报Error()
    {
        var report = Pipeline().Validate(Context("Deal {0} damage to {1}.", "造成伤害。"));

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
        Assert.Equal(ValidationCategory.Placeholder, issue.Category);
    }

    [Fact]
    public void Placeholder完整保留_不报Error()
    {
        var report = Pipeline().Validate(Context("Deal {0} damage to {1}.", "对{1}造成{0}点伤害。"));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
    }

    [Fact]
    public void 方括号标签_历史译文被本地化_不报PlaceholderError()
    {
        // [NOTE] 在旧汉化里常被译成 [介绍]，不是运行期占位符 → 必须不报 Error
        var report = Pipeline().Validate(Context("[NOTE] Something happened.", "[介绍] 发生了一些事。"));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
    }

    [Fact]
    public void 未恢复保护标记_报PlaceholderError()
    {
        var report = Pipeline().Validate(Context("Deal {0} damage.", "造成 __LT_PH_0001__ 伤害"));

        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
    }

    // ---------- Tag ----------

    [Fact]
    public void Tag丢失_报Error()
    {
        var report = Pipeline().Validate(Context("<b>Hello</b> world", "你好世界"));

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.TagMismatch);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Tag属性大小写差异_不报Error()
    {
        var report = Pipeline().Validate(
            Context("<color=#FFFFFF>Hello</color>", "<color=#ffffff>你好</color>"));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.TagMismatch);
    }

    [Fact]
    public void Tag补全保留_不报Error()
    {
        var report = Pipeline().Validate(
            Context("<size=95%>Big<br/>text</size>", "<size=95%>大<br/>文本</size>"));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.TagMismatch);
    }
    // ---------- Number / LineBreak / Length ----------

    [Fact]
    public void 数字异常_报Warning()
    {
        var report = Pipeline().Validate(Context("Deal 20 damage and gain 2 Haste.", "造成伤害并获得迅捷。"));

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.NumberMismatch);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void 数字一致_含千分位归一化_不报Warning()
    {
        Assert.DoesNotContain(
            Pipeline().Validate(Context("Deal 1,000 damage.", "造成 1000 点伤害。")).Issues,
            i => i.Code == ValidationIssueCodes.NumberMismatch);

        Assert.DoesNotContain(
            Pipeline().Validate(Context("Deal 3.5 damage (100%).", "造成 3.5 伤害（100%）。")).Issues,
            i => i.Code == ValidationIssueCodes.NumberMismatch);
    }

    [Fact]
    public void 换行异常_报Warning()
    {
        var report = Pipeline().Validate(Context("First line.\nSecond line.", "第一行第二行。"));

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.LineBreakMismatch);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void 换行一致_不报Warning()
    {
        Assert.DoesNotContain(
            Pipeline().Validate(Context("First line.\nSecond line.", "第一行。\n第二行。")).Issues,
            i => i.Code == ValidationIssueCodes.LineBreakMismatch);
    }

    [Fact]
    public void 长度极端异常_报Warning()
    {
        var report = Pipeline().Validate(Context(
            "This is a rather long English sentence that should be translated.",
            "短"));

        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.LengthAnomaly);
    }

    [Fact]
    public void 源文过短_跳过长度判断()
    {
        Assert.DoesNotContain(
            Pipeline().Validate(Context("Good!", "好")).Issues,
            i => i.Code == ValidationIssueCodes.LengthAnomaly);
    }

    // ---------- English / Korean residue ----------

    [Fact]
    public void 合法缩写HP_SP_EGO_不报EnglishResidue()
    {
        foreach (var translation in new[]
                 {
                     "消耗 HP 与 SP",
                     "触发 E.G.O",
                     "查看 UI 中的 ID",
                     "HP SP E.G.O ID UI",
                 })
        {
            var report = Pipeline().Validate(Context("Consume 10 HP and 10 SP, then trigger E.G.O.", translation));
            Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.EnglishResidue);
        }
    }

    [Fact]
    public void 明显英文整句残留_报Warning()
    {
        var report = Pipeline().Validate(Context(
            "对敌人造成伤害。",
            "对敌人造成 Deal damage to the enemy 效果。"));

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.EnglishResidue);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(ValidationCategory.Language, issue.Category);
    }

    [Fact]
    public void 单个英文单词残留_不报EnglishResidue()
    {
        var report = Pipeline().Validate(Context("对敌人造成伤害。", "对敌人造成 Enkephalin 伤害。"));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.EnglishResidue);
    }

    [Fact]
    public void 韩文残留_报Warning()
    {
        var report = Pipeline().Validate(Context("这是一个测试。", "这是一个 테스트 文本。"));

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.KoreanResidue);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    // ---------- Terminology ----------

    [Fact]
    public void locked术语正确译法_通过()
    {
        var terminology = new[]
        {
            new TerminologyRequirement { Source = "Sinking", Target = "沉沦", Locked = true },
        };
        var report = Pipeline().Validate(Context("Inflict 2 Sinking.", "施加 2 层沉沦。", terminology: terminology));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.TerminologyMismatch);
    }

    [Fact]
    public void locked术语错误译法_报Warning()
    {
        var terminology = new[]
        {
            new TerminologyRequirement { Source = "Sinking", Target = "沉沦", Locked = true },
        };
        var report = Pipeline().Validate(Context("Inflict 2 Sinking.", "施加 2 层下沉。", terminology: terminology));

        var issue = Assert.Single(report.Issues, i => i.Code == ValidationIssueCodes.TerminologyMismatch);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.Equal(ValidationCategory.Terminology, issue.Category);
    }

    [Fact]
    public void 术语子串误命中_单词边界过滤生效()
    {
        // "During" 里含子串 "Ring"（glossary 术语），但不应强制“环指”
        var terminology = new[]
        {
            new TerminologyRequirement { Source = "Ring", Target = "环指", Locked = true },
        };
        var report = Pipeline().Validate(Context("During the battle.", "战斗期间。", terminology: terminology));

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.TerminologyMismatch);
    }

    // ---------- 汇总 / 健壮性 ----------

    [Fact]
    public void 多Issue_保留结构化列表并汇总为可读字符串()
    {
        var report = Pipeline().Validate(Context("Deal 20 {0} damage.\nNext turn.", "造成伤害。"));

        Assert.True(report.Issues.Count >= 3);
        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.NumberMismatch);
        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.LineBreakMismatch);
        Assert.NotNull(report.Summary);
        Assert.Contains(ValidationIssueCodes.NumberMismatch, report.Summary);
        // Summary 只是展示层：结构化 Issue 不会被丢弃
        Assert.All(report.Issues, i => Assert.False(string.IsNullOrWhiteSpace(i.Code)));
    }

    [Fact]
    public void 所有Validator_对null与空白输入均不抛异常()
    {
        var pipeline = Pipeline();
        foreach (var source in new[] { null, string.Empty, "   " })
        {
            foreach (var translation in new[] { null, string.Empty, "   " })
            {
                var context = new ValidationContext
                {
                    Key = Key(),
                    SourceText = source!,
                    Translation = translation!,
                };

                var report = pipeline.Validate(context);
                Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.ValidatorFailure);
            }
        }
    }

    [Fact]
    public void 使用项目术语库_驱动TerminologyValidator()
    {
        var configDir = Path.Combine(Path.GetTempPath(), "LT_VALCFG_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        try
        {
            File.WriteAllText(
                Path.Combine(configDir, "glossary.json"),
                "{\"Sinking\":{\"translation\":\"沉沦\",\"locked\":true},\"Ring\":{\"translation\":\"环指\",\"locked\":true}}");

            var pipeline = ValidationPipeline.CreateDefault(configDir);

            // 正确译法：无 TERMINOLOGY_MISMATCH
            Assert.DoesNotContain(
                pipeline.Validate(Context("Inflict 2 Sinking.", "施加 2 层沉沦。")).Issues,
                i => i.Code == ValidationIssueCodes.TerminologyMismatch);

            // 错误译法：Warning
            Assert.Contains(
                pipeline.Validate(Context("Inflict 2 Sinking.", "施加 2 层下沉。")).Issues,
                i => i.Code == ValidationIssueCodes.TerminologyMismatch);
        }
        finally
        {
            Directory.Delete(configDir, true);
        }
    }

}
