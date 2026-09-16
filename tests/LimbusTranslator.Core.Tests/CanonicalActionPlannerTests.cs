using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Multilingual;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0B轮：韩文 Canonical Diff → TranslationAction（含首次基线迁移）。</summary>
public sealed class CanonicalActionPlannerTests
{
    private static TranslationUnit Unit(string kr, string record = "1")
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Items.json", RecordId = record, FieldPath = "dataList[0].name" },
            SourceText = kr,
            RecordId = record,
            FieldPath = "dataList[0].name",
            FilePath = "Items.json",
        };

    private static string KeyOf(string record = "1")
        => new UnitKey { RelativeFilePath = "Items.json", RecordId = record, FieldPath = "dataList[0].name" }.ToString();

    [Fact]
    public void KR未变_旧中文存在_继承()
    {
        var canonical = CanonicalDiffService.Compute(new[] { Unit("가") }, new[] { Unit("가") });
        var plan = CanonicalActionPlanner.Plan(canonical, new[] { KeyOf() }, isFirstCanonicalBaseline: false);

        Assert.Equal(TranslationAction.Inherit, plan.Actions[KeyOf()]);
        Assert.Equal(1, plan.Inherit);
    }

    [Fact]
    public void KR未变_旧中文缺失_TranslateMissing()
    {
        var canonical = CanonicalDiffService.Compute(new[] { Unit("가") }, new[] { Unit("가") });
        var plan = CanonicalActionPlanner.Plan(canonical, Array.Empty<string>(), false);

        Assert.Equal(TranslationAction.TranslateMissing, plan.Actions[KeyOf()]);
    }

    [Fact]
    public void KR新增修改删除_动作正确()
    {
        var added = CanonicalActionPlanner.Plan(
            CanonicalDiffService.Compute(Array.Empty<TranslationUnit>(), new[] { Unit("가") }),
            Array.Empty<string>(), false);
        Assert.Equal(TranslationAction.TranslateNew, added.Actions[KeyOf()]);

        var modified = CanonicalActionPlanner.Plan(
            CanonicalDiffService.Compute(new[] { Unit("가") }, new[] { Unit("나") }),
            Array.Empty<string>(), false);
        Assert.Equal(TranslationAction.TranslateModified, modified.Actions[KeyOf()]);

        var deleted = CanonicalActionPlanner.Plan(
            CanonicalDiffService.Compute(new[] { Unit("가") }, Array.Empty<TranslationUnit>()),
            Array.Empty<string>(), false);
        Assert.Equal(TranslationAction.SkipDeleted, deleted.Actions[KeyOf()]);
    }

    [Fact]
    public void EN变化_KR未变_仍为继承_不得TranslateModified()
    {
        var canonical = CanonicalDiffService.Compute(
            new[] { Unit("가") }, new[] { Unit("가") },
            new[] { Unit("A") }, new[] { Unit("A changed") });

        Assert.Equal(1, canonical.EnglishChangedCount);
        Assert.Equal(0, canonical.Modified);

        var plan = CanonicalActionPlanner.Plan(canonical, new[] { KeyOf() }, false);
        Assert.Equal(TranslationAction.Inherit, plan.Actions[KeyOf()]);
        Assert.Equal(0, plan.TranslateModified);
    }

    [Fact]
    public void JP变化_KR未变_仍为继承()
    {
        var canonical = CanonicalDiffService.Compute(
            new[] { Unit("가") }, new[] { Unit("가") }, null, null,
            new[] { Unit("あ") }, new[] { Unit("い") });

        Assert.Equal(1, canonical.JapaneseChangedCount);
        var plan = CanonicalActionPlanner.Plan(canonical, new[] { KeyOf() }, false);
        Assert.Equal(TranslationAction.Inherit, plan.Actions[KeyOf()]);
    }

    [Fact]
    public void 首次基线迁移_不得产生任何TranslateNew()
    {
        var canonical = CanonicalDiffService.Compute(
            Array.Empty<TranslationUnit>(),
            new[] { Unit("가"), Unit("나", record: "2") });

        var plan = CanonicalActionPlanner.Plan(canonical, new[] { KeyOf() }, isFirstCanonicalBaseline: true);

        Assert.True(plan.IsBaselineMigration);
        Assert.Equal(0, plan.TranslateNew);
        Assert.Equal(0, plan.TranslateModified);
        Assert.Equal(1, plan.Inherit);
        Assert.Equal(1, plan.TranslateMissing);
    }
}