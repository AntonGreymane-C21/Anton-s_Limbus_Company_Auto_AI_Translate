using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Validation;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第2轮：来源感知校验策略测试。
/// 覆盖：Issues 与 NeedsReview 分层、AI / 人工 / 继承的不同决策。
/// </summary>
public class ValidationSourceAwareTests
{
    private static DiffEntry Entry(
        string source,
        string translation,
        TranslationSource? provenance,
        TranslationAction action = TranslationAction.TranslateNew)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
            NewSourceText = source,
            DiffKind = action == TranslationAction.Inherit ? DiffKind.Unchanged : DiffKind.Added,
            Action = action,
            Translation = translation,
            Provenance = provenance,
        };

    private static ValidationPipeline Pipeline(ValidationOptions? options = null)
        => new(null, null, options ?? ValidationOptions.Default);

    [Fact]
    public void 策略解析_按译文来源返回对应策略()
    {
        Assert.Equal(ValidationPolicy.Full, ValidationPolicyResolver.Resolve(TranslationSource.AI));
        Assert.Equal(ValidationPolicy.Full, ValidationPolicyResolver.Resolve(TranslationSource.Mock));
        Assert.Equal(ValidationPolicy.Full, ValidationPolicyResolver.Resolve(TranslationSource.TranslationMemory));
        Assert.Equal(ValidationPolicy.Full, ValidationPolicyResolver.Resolve(null));
        Assert.Equal(ValidationPolicy.HardSafetyOnly, ValidationPolicyResolver.Resolve(TranslationSource.HumanReviewed));
        Assert.Equal(ValidationPolicy.HardSafetyOnly, ValidationPolicyResolver.Resolve(TranslationSource.Official));
        Assert.Equal(ValidationPolicy.HardSafetyOnly, ValidationPolicyResolver.Resolve(TranslationSource.Imported));
        Assert.Equal(
            ValidationPolicy.InheritedStructureOnly,
            ValidationPolicyResolver.Resolve(TranslationSource.Inherited));
    }

    [Fact]
    public void AI加Warning_标记NeedsReview()
    {
        var entry = Entry("Deal 20 damage to the enemy.", "对敌人造成伤害。", TranslationSource.AI);
        var pipeline = Pipeline();

        var report = pipeline.ValidateAndApply(entry);

        Assert.Equal(ValidationPolicy.Full, report.Policy);
        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.NumberMismatch);
        Assert.True(entry.NeedsReview);
        Assert.NotNull(entry.ReviewReason);
        Assert.NotEmpty(entry.ValidationIssues);
    }

    [Fact]
    public void AI加Error_标记NeedsReview()
    {
        var entry = Entry("Deal {0} damage to the enemy.", "对敌人造成伤害。", TranslationSource.AI);
        var pipeline = Pipeline();

        var report = pipeline.ValidateAndApply(entry);

        Assert.True(report.HasError);
        Assert.True(entry.NeedsReview);
    }

    [Fact]
    public void AI无问题_保持不标记NeedsReview()
    {
        var entry = Entry("Deal 20 damage to the enemy.", "对敌人造成 20 点伤害。", TranslationSource.AI);
        var pipeline = Pipeline();

        pipeline.ValidateAndApply(entry);

        Assert.False(entry.NeedsReview);
        Assert.Null(entry.ReviewReason);
    }
    [Fact]
    public void HumanReviewed加普通Warning_不失去人工确认语义()
    {
        // 译文含英文整句 → 启发式 Warning；但来源是人工确认，不得因此变回待审核
        var entry = Entry(
            "对敌人造成伤害。",
            "对敌人造成 Deal damage to the enemy 效果。",
            TranslationSource.HumanReviewed);
        entry.NeedsReview = false;
        var pipeline = Pipeline();

        var report = pipeline.ValidateAndApply(entry);

        Assert.Equal(ValidationPolicy.HardSafetyOnly, report.Policy);
        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.EnglishResidue);
        Assert.False(entry.NeedsReview);
        Assert.Null(entry.ReviewReason);
        // 启发式问题仍然保留在结构化列表里，供 UI / ReleaseGate 查看
        Assert.Contains(entry.ValidationIssues, i => i.Code == ValidationIssueCodes.EnglishResidue);
    }

    [Fact]
    public void HumanReviewed加PlaceholderError_必须暴露严重问题()
    {
        var entry = Entry("Deal {0} damage to the enemy.", "对敌人造成伤害。", TranslationSource.HumanReviewed);
        entry.NeedsReview = false;
        var pipeline = Pipeline();

        var report = pipeline.ValidateAndApply(entry);

        Assert.True(report.HasError);
        Assert.True(entry.NeedsReview);
        Assert.Contains(entry.ValidationIssues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
    }

    [Fact]
    public void Inherited加结构Error_保留Issue但不自动变成待审核()
    {
        var entry = Entry(
            "Deal {0} damage to the enemy.",
            "对敌人造成伤害。",
            TranslationSource.Inherited,
            TranslationAction.Inherit);
        entry.NeedsReview = false;
        var pipeline = Pipeline();

        var report = pipeline.ValidateAndApply(entry);

        Assert.Equal(ValidationPolicy.InheritedStructureOnly, report.Policy);
        Assert.True(report.HasError);
        // 本轮：历史继承条目只产出 Issue，不因结构差异被自动标记（避免海量历史条目涌入审核）
        Assert.False(entry.NeedsReview);
        Assert.Contains(entry.ValidationIssues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
    }

    [Fact]
    public void 继承策略下_启发式规则默认不执行()
    {
        var entry = Entry(
            "对敌人造成伤害。",
            "对敌人造成 Deal damage to the enemy 效果。",
            TranslationSource.Inherited,
            TranslationAction.Inherit);
        var pipeline = Pipeline();

        var report = pipeline.ValidateAndApply(entry);

        Assert.DoesNotContain(report.Issues, i => i.Code == ValidationIssueCodes.EnglishResidue);
        Assert.DoesNotContain(report.Validators, name => name == nameof(EnglishResidueValidator));
    }

    [Fact]
    public void Provider已有Issue_合并进报告()
    {
        var entry = Entry("Deal 20 damage.", "对敌人造成 20 点伤害。", TranslationSource.AI);
        var preexisting = new[]
        {
            new ValidationIssue
            {
                Key = entry.Key,
                Code = ValidationIssueCodes.PlaceholderMismatch,
                Severity = ValidationSeverity.Error,
                Category = ValidationCategory.Placeholder,
                Validator = "PlaceholderProtector",
                Message = "Placeholder 宽容恢复: 缺失 __LT_PH_0001__",
            },
        };
        var pipeline = Pipeline();

        var report = pipeline.ValidateAndApply(entry, preexisting);

        Assert.Contains(report.Issues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
        Assert.True(entry.NeedsReview);
    }

}
