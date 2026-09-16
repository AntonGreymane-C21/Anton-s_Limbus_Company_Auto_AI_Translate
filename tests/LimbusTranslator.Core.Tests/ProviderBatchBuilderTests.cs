using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第7.5轮：Provider 请求分批器测试（唯一分批实现）。
/// 覆盖：条目上限、字符预算、单条超预算、空输入、顺序稳定性。
/// </summary>
public class ProviderBatchBuilderTests
{
    private static DiffEntry MakeEntry(int index, string source, string? oldSource = null, string? oldTranslation = null)
        => new()
        {
            Key = new UnitKey
            {
                RelativeFilePath = "Test.json",
                RecordId = index.ToString(),
                FieldPath = $"dataList[{index}].name",
            },
            NewSourceText = source,
            OldSourceText = oldSource,
            OldTranslation = oldTranslation,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };

    private static List<DiffEntry> MakeEntries(int count, string source = "abc")
        => Enumerable.Range(0, count).Select(i => MakeEntry(i, source)).ToList();

    private static int[] Sizes(IReadOnlyList<IReadOnlyList<DiffEntry>> batches)
        => batches.Select(b => b.Count).ToArray();

    [Fact]
    public void 空输入_返回0个批次()
    {
        var batches = ProviderBatchBuilder.Build(Array.Empty<DiffEntry>(), BatchOptions.Default);

        Assert.Empty(batches);
    }

    [Fact]
    public void 单条_1个批次()
    {
        var batches = ProviderBatchBuilder.Build(MakeEntries(1), BatchOptions.Default);

        Assert.Equal(new[] { 1 }, Sizes(batches));
    }

    [Fact]
    public void 默认配置_20条一批_21条分两批()
    {
        var twenty = ProviderBatchBuilder.Build(MakeEntries(20), BatchOptions.Default);
        var twentyOne = ProviderBatchBuilder.Build(MakeEntries(21), BatchOptions.Default);

        Assert.Equal(new[] { 20 }, Sizes(twenty));
        Assert.Equal(new[] { 20, 1 }, Sizes(twentyOne));
    }

    [Fact]
    public void MaxItems为3_8条_分为3批()
    {
        var options = new BatchOptions { MaxItemsPerBatch = 3 };

        var batches = ProviderBatchBuilder.Build(MakeEntries(8), options);

        Assert.Equal(new[] { 3, 3, 2 }, Sizes(batches));
    }

    [Fact]
    public void 字符预算_等于Source加OldSource加OldTranslation长度()
    {
        var entry = MakeEntry(0, "12345", "123", "1234567");

        Assert.Equal(15, BatchOptions.MeasureItemCharacters(entry));
        Assert.Equal(0, BatchOptions.MeasureItemCharacters(MakeEntry(1, string.Empty)));
    }

    [Fact]
    public void 字符预算达到上限_按上限切分()
    {
        // 每条 5 字符，预算 12 → 每条 2 条（10 ≤ 12），第 3 条起新批
        var options = new BatchOptions { MaxCharactersPerBatch = 12 };
        var entries = MakeEntries(5, "12345");

        var batches = ProviderBatchBuilder.Build(entries, options);

        Assert.Equal(new[] { 2, 2, 1 }, Sizes(batches));
    }

    [Fact]
    public void 单条超过字符预算_单独成批且不截断()
    {
        var options = new BatchOptions { MaxCharactersPerBatch = 10 };
        var longEntry = MakeEntry(0, new string('x', 50));
        var normal = MakeEntry(1, "abc");
        var logs = new List<string>();

        var batches = ProviderBatchBuilder.Build(new[] { longEntry, normal }, options, logs.Add);

        Assert.Equal(new[] { 1, 1 }, Sizes(batches));
        Assert.Same(longEntry, batches[0][0]);
        Assert.Equal(50, batches[0][0].NewSourceText!.Length);          // 原文未被截断
        Assert.Contains(logs, l => l.Contains("超过批次字符上限") && l.Contains("单独提交"));
    }

    [Fact]
    public void 超长条目后续条目仍正常分批()
    {
        var options = new BatchOptions { MaxItemsPerBatch = 2, MaxCharactersPerBatch = 10 };
        var entries = new List<DiffEntry>
        {
            MakeEntry(0, new string('y', 40)),   // 超长 → 单独
            MakeEntry(1, "aaa"),
            MakeEntry(2, "bbb"),
            MakeEntry(3, "ccc"),
        };

        var batches = ProviderBatchBuilder.Build(entries, options);

        Assert.Equal(new[] { 1, 2, 1 }, Sizes(batches));
        Assert.Equal("dataList[0].name", batches[0][0].Key.FieldPath);
        Assert.Equal("dataList[1].name", batches[1][0].Key.FieldPath);
    }

    [Fact]
    public void 批次边界与批内顺序稳定可复现()
    {
        var options = new BatchOptions { MaxItemsPerBatch = 4, MaxCharactersPerBatch = 50 };
        var entries = MakeEntries(11, "abcdef");

        var first = ProviderBatchBuilder.Build(entries, options);
        var second = ProviderBatchBuilder.Build(entries, options);

        static string Describe(IReadOnlyList<IReadOnlyList<DiffEntry>> batches)
            => string.Join("|", batches.Select(b => string.Join(",", b.Select(e => e.Key.FieldPath))));

        Assert.Equal(Describe(first), Describe(second));
        Assert.Equal(new[] { 4, 4, 3 }, Sizes(first));
    }
}
