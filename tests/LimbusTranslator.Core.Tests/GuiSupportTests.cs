using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Review;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.85轮：GUI 支撑组件的可测试部分（筛选 / 部署状态映射 / Trace 实际 Thinking 统计）。
/// </summary>
public class GuiSupportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LT_GUI_" + Guid.NewGuid().ToString("N"));

    public GuiSupportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // 忽略
        }
    }

    private static DiffEntry Entry(params ValidationIssue[] issues)
    {
        var key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" };
        return new DiffEntry
        {
            Key = key,
            NewSourceText = "필립 싱클레어가 탈출장치로 후퇴",
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.TranslateMissing,
            Translation = "菲利普·辛克莱撤退",
            NeedsReview = true,
            ValidationIssues = issues,
        };
    }

    private static ValidationIssue Issue(string code, ValidationSeverity severity = ValidationSeverity.Warning)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].content" },
            Code = code,
            Severity = severity,
            Category = ValidationCategory.Language,
            Validator = "Test",
            Message = code,
        };

    [Fact]
    public void 筛选_NeedsReview与Error与Warning()
    {
        var entry = Entry(
            Issue(ValidationIssueCodes.SourceLanguageAnomaly),
            Issue(ValidationIssueCodes.TagMismatch, ValidationSeverity.Error));

        Assert.True(ReviewFilter.Matches(entry, ReviewFilterKind.All));
        Assert.True(ReviewFilter.Matches(entry, ReviewFilterKind.NeedsReview));
        Assert.True(ReviewFilter.Matches(entry, ReviewFilterKind.Error));
        Assert.True(ReviewFilter.Matches(entry, ReviewFilterKind.Warning));
        Assert.True(ReviewFilter.Matches(entry, ReviewFilterKind.SourceLanguageAnomaly));
        Assert.True(ReviewFilter.Matches(entry, ReviewFilterKind.TagMismatch));
        Assert.False(ReviewFilter.Matches(entry, ReviewFilterKind.KoreanResidue));
        Assert.False(ReviewFilter.Matches(entry, ReviewFilterKind.TerminologyMismatch));
        Assert.False(ReviewFilter.Matches(entry, ReviewFilterKind.EnglishResidue));
    }

    [Fact]
    public void 筛选_按Code精确匹配()
    {
        var korean = Entry(Issue(ValidationIssueCodes.KoreanResidue));
        var terminology = Entry(Issue(ValidationIssueCodes.TerminologyMismatch));
        var english = Entry(Issue(ValidationIssueCodes.EnglishResidue));

        Assert.True(ReviewFilter.Matches(korean, ReviewFilterKind.KoreanResidue));
        Assert.True(ReviewFilter.Matches(terminology, ReviewFilterKind.TerminologyMismatch));
        Assert.True(ReviewFilter.Matches(english, ReviewFilterKind.EnglishResidue));
        Assert.False(ReviewFilter.Matches(korean, ReviewFilterKind.EnglishResidue));
    }

    [Fact]
    public void 筛选_统计Error与Warning数量与硬安全判定()
    {
        var entries = new[]
        {
            Entry(
                Issue(ValidationIssueCodes.TagMismatch, ValidationSeverity.Error),
                Issue(ValidationIssueCodes.SourceLanguageAnomaly)),
            Entry(Issue(ValidationIssueCodes.KoreanResidue)),
        };

        var (error, warning) = ReviewFilter.CountSeverities(entries);

        Assert.Equal(1, error);
        Assert.Equal(2, warning);
        Assert.True(ReviewFilter.HasHardSafetyError(entries[0]));
        Assert.False(ReviewFilter.HasHardSafetyError(entries[1]));
    }

    [Fact]
    public void 筛选_显示文本与类型一一对应()
    {
        Assert.Equal(ReviewFilterKind.All, ReviewFilter.Parse(ReviewFilter.DisplayNames[0]));
        Assert.Equal(ReviewFilterKind.SourceLanguageAnomaly, ReviewFilter.Parse("只看 SOURCE_LANGUAGE_ANOMALY"));
        // 第8.88轮：新增筛选追加在末尾，因此 TagMismatch 不再是最后一项
        Assert.Equal(ReviewFilterKind.TagMismatch, ReviewFilter.Parse("只看 TAG_MISMATCH"));
        Assert.Equal(ReviewFilterKind.DefaultWorkSet, ReviewFilter.Parse(ReviewFilter.DisplayNames[^1]));
        Assert.Equal(ReviewFilterKind.Modified, ReviewFilter.Parse("只看 Modified"));
        Assert.Equal(ReviewFilterKind.All, ReviewFilter.Parse("不存在的筛选"));
        Assert.Equal(ReviewFilter.DisplayNames.Count, Enum.GetValues<ReviewFilterKind>().Length);
    }

    [Fact]
    public void 部署状态映射_三种状态文案与高危标记()
    {
        Assert.Equal("部署成功", DeployStatusText.Describe(DeploymentStatus.Succeeded));
        Assert.Contains("已完整回滚", DeployStatusText.Describe(DeploymentStatus.FailedRolledBack));
        Assert.Contains("回滚不完整", DeployStatusText.Describe(DeploymentStatus.FailedRollbackIncomplete));

        Assert.False(DeployStatusText.IsCritical(DeploymentStatus.Succeeded));
        Assert.False(DeployStatusText.IsCritical(DeploymentStatus.FailedRolledBack));
        Assert.True(DeployStatusText.IsCritical(DeploymentStatus.FailedRollbackIncomplete));
    }

    // ---------------- TranslationTraceStats ----------------

    private string WriteTrace(params string[] lines)
    {
        var path = Path.Combine(_root, "trace.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void Trace统计_聚合Token与网络缓存请求()
    {
        var path = WriteTrace(
            """{"networkCalled":true,"cacheHit":false,"inputTokens":100,"outputTokens":60,"reasoningTokens":45,"visibleOutputTokens":15,"totalTokens":160,"durationMs":9817,"retryCount":0,"fingerprint":"v2:aaa","thinkingEnabled":true,"thinkingPolicyReason":"StoryData"}""",
            """{"networkCalled":false,"cacheHit":true,"inputTokens":50,"outputTokens":20,"totalTokens":70,"durationMs":100,"retryCount":1,"thinkingEnabled":false,"thinkingPolicyReason":"DefaultOff"}""");

        var stats = TranslationTraceStats.Read(path);

        Assert.Equal(1, stats.NetworkCalled);
        Assert.Equal(1, stats.CacheHit);
        Assert.Equal(2, stats.RequestCount);
        Assert.Equal(150, stats.InputTokens);
        Assert.Equal(80, stats.OutputTokens);
        Assert.Equal(45, stats.ReasoningTokens);
        Assert.Equal(15, stats.VisibleOutputTokens);
        Assert.Equal(230, stats.TotalTokens);
        Assert.Equal(9917, stats.DurationMs);
        Assert.Equal(1, stats.RetryCount);
        Assert.Equal("v2:aaa", stats.FirstFingerprint);
    }

    [Fact]
    public void Trace统计_实际Thinking分布按原因码统计()
    {
        var path = WriteTrace(
            """{"networkCalled":true,"thinkingEnabled":true,"thinkingPolicyReason":"SourceLanguageAnomaly"}""",
            """{"networkCalled":true,"thinkingEnabled":true,"thinkingPolicyReason":"SourceLanguageAnomaly"}""",
            """{"networkCalled":true,"thinkingEnabled":true,"thinkingPolicyReason":"StoryData"}""",
            """{"networkCalled":true,"thinkingEnabled":true,"thinkingPolicyReason":"AlwaysOn"}""",
            """{"networkCalled":true,"thinkingEnabled":false,"thinkingPolicyReason":"DefaultOff"}""");

        var stats = TranslationTraceStats.Read(path);

        Assert.Equal(2, stats.ThinkingOnSourceLanguageAnomaly);
        Assert.Equal(1, stats.ThinkingOnStoryData);
        Assert.Equal(1, stats.ThinkingOnOther);
        Assert.Equal(1, stats.ThinkingOff);
    }

    [Fact]
    public void Trace统计_文件不存在或坏行不抛异常()
    {
        Assert.Equal(0, TranslationTraceStats.Read(Path.Combine(_root, "missing.jsonl")).RequestCount);

        var path = WriteTrace("{ 这不是合法 JSON", """{"networkCalled":true,"thinkingEnabled":false}""");
        var stats = TranslationTraceStats.Read(path);

        Assert.Equal(1, stats.NetworkCalled);   // 坏行被跳过，好行仍统计
        Assert.Equal(1, stats.ThinkingOff);
    }
}
