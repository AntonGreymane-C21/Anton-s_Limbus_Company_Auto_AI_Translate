using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第3轮：来源感知的发布门禁策略测试（纯函数，不访问磁盘）。
/// </summary>
public class ReleaseGateTests
{
    private static DiffEntry Entry(
        TranslationSource? provenance,
        bool needsReview = false,
        params ValidationIssue[] issues)
    {
        var key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" };
        return new DiffEntry
        {
            Key = key,
            NewSourceText = "Deal 20 {0} damage.",
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
            Translation = "对敌人造成伤害。",
            Provenance = provenance,
            NeedsReview = needsReview,
            ValidationIssues = issues,
        };
    }

    private static ValidationIssue Issue(
        string code,
        ValidationSeverity severity,
        ValidationCategory category = ValidationCategory.Structure)
    {
        var key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" };
        return new ValidationIssue
        {
            Key = key,
            Code = code,
            Severity = severity,
            Category = category,
            Validator = "TestValidator",
            Message = code + " 说明",
        };
    }

    private static ValidationIssue HardError() =>
        Issue(ValidationIssueCodes.PlaceholderMismatch, ValidationSeverity.Error, ValidationCategory.Placeholder);

    private static ValidationIssue Warning() =>
        Issue(ValidationIssueCodes.NumberMismatch, ValidationSeverity.Warning, ValidationCategory.Format);

    [Fact]
    public void 无问题时_Passed()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.AI) });

        Assert.Equal(ReleaseGateStatus.Passed, result.Status);
        Assert.Equal(0, result.ErrorCount);
        Assert.Empty(result.BlockingReasons);
    }

    [Fact]
    public void AI出现Error_Blocked()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.AI, false, HardError()) });

        Assert.Equal(ReleaseGateStatus.Blocked, result.Status);
        Assert.Equal(1, result.BlockingErrorCount);
        Assert.Contains(result.Reasons, r => r.Kind == ReleaseGateReasonKinds.NewTranslationHardError
            && r.Escalation == ReleaseGateStatus.Blocked);
        Assert.Contains(result.BlockingReasons, r => r.Contains("禁止直接部署"));
    }

    [Fact]
    public void AI出现Warning_RequiresConfirmation()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.AI, false, Warning()) });

        Assert.Equal(ReleaseGateStatus.RequiresConfirmation, result.Status);
        Assert.Equal(0, result.BlockingErrorCount);
        Assert.NotNull(result.RequiresConfirmationReason);
    }

    [Fact]
    public void AI待审核_RequiresConfirmation()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.AI, needsReview: true) });

        Assert.Equal(ReleaseGateStatus.RequiresConfirmation, result.Status);
        Assert.Equal(1, result.NeedsReviewCount);
        Assert.Contains(result.Reasons, r => r.Kind == ReleaseGateReasonKinds.NeedsReview);
    }

    [Fact]
    public void Mock与TranslationMemory_与AI同等处理()
    {
        Assert.Equal(
            ReleaseGateStatus.Blocked,
            ReleaseGate.Evaluate(new[] { Entry(TranslationSource.Mock, false, HardError()) }).Status);

        Assert.Equal(
            ReleaseGateStatus.RequiresConfirmation,
            ReleaseGate.Evaluate(new[] { Entry(TranslationSource.TranslationMemory, false, Warning()) }).Status);
    }
    [Fact]
    public void HumanReviewed硬安全Error_Blocked()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.HumanReviewed, false, HardError()) });

        Assert.Equal(ReleaseGateStatus.Blocked, result.Status);
        Assert.Contains(result.Reasons, r => r.Kind == ReleaseGateReasonKinds.ReviewedHardError);
        Assert.Contains(result.BlockingReasons, r => r.Contains("人工审核不能跳过结构安全校验"));
    }

    [Fact]
    public void HumanReviewed启发式Warning_不Block()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.HumanReviewed, false, Warning()) });

        Assert.Equal(ReleaseGateStatus.Passed, result.Status);
        Assert.Equal(1, result.WarningCount);
        Assert.Contains(result.Reasons, r => r.Kind == ReleaseGateReasonKinds.ReviewedWarning
            && r.Escalation == ReleaseGateStatus.Passed);
    }

    [Fact]
    public void 历史继承硬安全Error_首版为RequiresConfirmation()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.Inherited, false, HardError()) });

        Assert.Equal(ReleaseGateStatus.RequiresConfirmation, result.Status);
        Assert.Equal(1, result.HistoricalInheritedErrorCount);
        Assert.Equal(0, result.BlockingErrorCount);
        Assert.Contains(result.Reasons, r => r.Kind == ReleaseGateReasonKinds.HistoricalInheritedError
            && r.Escalation == ReleaseGateStatus.RequiresConfirmation);
        // 必须提示“这些问题在本次 Validator 上线前已经存在”
        Assert.Contains(result.Reasons, r => r.Message.Contains("在本次 Validator 上线前已经存在"));
    }

    [Fact]
    public void 历史继承启发式Warning_仅统计不升级()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.Inherited, false, Warning()) });

        Assert.Equal(ReleaseGateStatus.Passed, result.Status);
        Assert.Equal(1, result.WarningCount);
        Assert.Contains(result.Reasons, r => r.Kind == ReleaseGateReasonKinds.HistoricalInheritedWarning);
    }

    [Fact]
    public void 混合来源_取最严格结论()
    {
        var entries = new[]
        {
            Entry(TranslationSource.Inherited, false, HardError()),
            Entry(TranslationSource.AI, false, Warning()),
        };

        var result = ReleaseGate.Evaluate(entries);

        Assert.Equal(ReleaseGateStatus.RequiresConfirmation, result.Status);
        Assert.Equal(1, result.HistoricalInheritedErrorCount);
    }

    [Fact]
    public void 策略收紧后_历史继承Error可以改为Blocked()
    {
        var policy = new ReleaseGatePolicy { InheritedHardSafetyError = ReleaseGateStatus.Blocked };

        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.Inherited, false, HardError()) }, policy);

        Assert.Equal(ReleaseGateStatus.Blocked, result.Status);
        // 策略可配置：无需改动任何 Validator
        Assert.Equal(ReleaseGateStatus.RequiresConfirmation,
            ReleaseGate.Evaluate(new[] { Entry(TranslationSource.Inherited, false, HardError()) }).Status);
    }

    [Fact]
    public void 原因包含可定位样本()
    {
        var result = ReleaseGate.Evaluate(new[] { Entry(TranslationSource.AI, false, HardError()) });

        var reason = Assert.Single(result.Reasons, r => r.Kind == ReleaseGateReasonKinds.NewTranslationHardError);
        var sample = Assert.Single(reason.Samples);
        Assert.Equal("Test.json", sample.RelativeFilePath);
        Assert.Contains("Test.json|1|dataList[0].content", sample.UnitKey);
        Assert.Equal(ValidationIssueCodes.PlaceholderMismatch, sample.Code);
        Assert.Equal(ValidationSeverity.Error, sample.Severity);
        Assert.False(string.IsNullOrWhiteSpace(sample.Message));
    }

    [Fact]
    public void 已删除条目_不参与门禁评估()
    {
        var key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "9", FieldPath = "dataList[9].content" };
        var deleted = new DiffEntry
        {
            Key = key,
            DiffKind = DiffKind.Deleted,
            Action = TranslationAction.SkipDeleted,
            ValidationIssues = new[] { HardError() },
        };

        var result = ReleaseGate.Evaluate(new[] { deleted });

        Assert.Equal(ReleaseGateStatus.Passed, result.Status);
        Assert.Equal(0, result.EvaluatedEntryCount);
    }

}
