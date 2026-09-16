using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Glossary;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8轮：真实 API 冒烟选择器的本地安全测试。
/// 不访问真实数据库、真实 API 或游戏目录。
/// </summary>
public class RealApiSmokeSelectorTests
{
    [Fact]
    public void 正常候选_固定选择20条且预计批次可复现()
    {
        var entries = MakeEntries(35);

        var first = RealApiSmokeSelector.Select(
            entries,
            TranslationContextBuilder.Disabled,
            GlossaryService.DefaultEntries,
            targetItemCount: 20);
        var second = RealApiSmokeSelector.Select(
            entries,
            TranslationContextBuilder.Disabled,
            GlossaryService.DefaultEntries,
            targetItemCount: 20);

        Assert.Equal(20, first.Entries.Count);
        Assert.Equal(
            first.Entries.Select(entry => entry.Key.ToString()),
            second.Entries.Select(entry => entry.Key.ToString()));

        var plan = RealApiSmokeSelector.CreatePlan(
            "20260914_120000_test",
            first,
            new BatchOptions { MaxItemsPerBatch = 10, MaxCharactersPerBatch = 30_000 });
        Assert.Equal(20, plan.ItemCount);
        Assert.Equal(2, plan.ExpectedProviderBatchCount);
        Assert.All(plan.Items, item => Assert.Equal(64, item.SourceHash.Length));
    }

    [Fact]
    public void 超过30条目标_在选择前拒绝()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            RealApiSmokeSelector.Select(
                MakeEntries(35),
                TranslationContextBuilder.Disabled,
                GlossaryService.DefaultEntries,
                targetItemCount: 31));

        Assert.Contains("10 ~ 30", error.Message);
    }

    [Fact]
    public void 计划源文本变化_拒绝复用相同样本()
    {
        var entries = MakeEntries(12);
        var selection = RealApiSmokeSelector.Select(
            entries,
            TranslationContextBuilder.Disabled,
            GlossaryService.DefaultEntries,
            targetItemCount: 10);
        var plan = RealApiSmokeSelector.CreatePlan("20260914_120000_test", selection, BatchOptions.Default);

        var changed = MakeEntries(12);
        changed[0] = MakeEntry(0, "已发生变化的源文本");

        var error = Assert.Throws<InvalidOperationException>(() =>
            RealApiSmokeSelector.ResolvePlan(plan, changed, TranslationContextBuilder.Disabled));

        Assert.Contains("SourceHash 已变化", error.Message);
    }

    private static List<DiffEntry> MakeEntries(int count)
        => Enumerable.Range(0, count)
            .Select(index => MakeEntry(index, index == 1 ? "Inflict Sinking" : $"Source text {index:D2}"))
            .ToList();

    private static DiffEntry MakeEntry(int index, string source)
        => new()
        {
            Key = new UnitKey
            {
                RelativeFilePath = "Smoke.json",
                RecordId = index.ToString(),
                FieldPath = $"dataList[{index}].name",
            },
            NewSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
            Order = index,
        };
}
