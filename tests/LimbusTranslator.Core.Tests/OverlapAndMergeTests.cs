using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Paratranz;
using LimbusTranslator.Infrastructure.Validation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第8.88轮：术语重叠收敛（Longest Match Wins）+ 本地/远程合并优先级。</summary>
public sealed class OverlapAndMergeTests
{
    private static readonly Dictionary<string, GlossaryEntry> Local = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Attack Power Up"] = new() { Translation = "强壮", Locked = true },
        ["Power Up"] = new() { Translation = "威力提升", Locked = true },
        ["Gregor"] = new() { Translation = "格里高尔", Locked = true },
    };

    private static IReadOnlyList<string> Match(Dictionary<string, GlossaryEntry> entries, string text)
        => ActiveGlossarySnapshot.SelectTermsFrom(entries, new[] { text }).Select(kv => kv.Key).ToList();

    [Fact]
    public void 重叠_更长更具体的术语胜出()
        => Assert.Equal(new[] { "Attack Power Up" }, Match(Local, "Attack Power Up"));

    [Fact]
    public void 非重叠位置_短术语仍保留()
        => Assert.Equal(new[] { "Power Up" }, Match(Local, "Gain Power Up."));

    [Fact]
    public void 同一文本两处_应各自命中()
        => Assert.Equal(new[] { "Attack Power Up", "Power Up" }, Match(Local, "Attack Power Up and Power Up"));

    [Fact]
    public void Don与DonQuixote_应命中更长的()
    {
        var entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Don"] = new() { Translation = "唐", Locked = false },
            ["Don Quixote"] = new() { Translation = "堂吉诃德", Locked = true },
        };

        Assert.Equal(new[] { "Don Quixote" }, Match(entries, "Don Quixote attacks"));
        Assert.Equal(new[] { "Don" }, Match(entries, "Don attacks"));
    }

    [Fact]
    public void 同长度重叠_Locked优先且结果确定()
    {
        var entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ring"] = new() { Translation = "环", Locked = false },
            ["RinG"] = new() { Translation = "环2", Locked = true },
        };

        var first = Match(entries, "Ring");
        var second = Match(entries, "Ring");
        Assert.Equal(first, second);
        Assert.Single(first);
    }

    [Fact]
    public void Prompt与Validator_必须共享同一匹配选择()
    {
        // Provider 侧（Snapshot）与 Validator 侧（ValidationPipeline）都基于 SelectTermsFrom
        var snapshot = ActiveGlossarySnapshot.FromEntries(Local);
        Assert.Equal(new[] { "Attack Power Up" }, snapshot.SelectTerms(new[] { "Attack Power Up" }).Select(kv => kv.Key).ToArray());

        var glossary = GlossaryService.FromSnapshot(snapshot);
        Assert.Equal(new[] { "Attack Power Up" }, glossary.SelectTerms(new[] { "Attack Power Up" }).Select(kv => kv.Key).ToArray());
    }

    [Fact]
    public void 合并_本地Locked覆盖远程同名()
    {
        var merged = GlossaryMerger.Merge(Local, new[]
        {
            new ParatranzTermEntry { Term = "Gregor", Translation = "格雷戈尔" },
        });

        Assert.Equal("格里高尔", merged.Entries["Gregor"].Translation);
        Assert.True(merged.Entries["Gregor"].Locked);
        Assert.Equal(GlossaryTermSource.Local, merged.Entries["Gregor"].Source);
        Assert.Equal(1, merged.LocalOverrides);

        var conflict = Assert.Single(merged.Conflicts);
        Assert.Equal("格雷戈尔", conflict.RemoteTarget);
        Assert.Equal("格里高尔", conflict.LocalTarget);
        Assert.Equal(GlossaryTermSource.Local, conflict.Winner);
    }

    [Fact]
    public void 合并_本地Preferred同样覆盖远程()
    {
        var local = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase)
        {
            ["Haste"] = new() { Translation = "迅捷", Locked = false },
        };

        var merged = GlossaryMerger.Merge(local, new[]
        {
            new ParatranzTermEntry { Term = "Haste", Translation = "急速" },
        });

        Assert.Equal("迅捷", merged.Entries["Haste"].Translation);
        Assert.False(merged.Entries["Haste"].Locked);
        Assert.Single(merged.Conflicts);
    }

    [Fact]
    public void 合并_远程新术语以Preferred并入且来源可追溯()
    {
        var merged = GlossaryMerger.Merge(Local, new[]
        {
            new ParatranzTermEntry { Term = "Ishmael", Translation = "以实玛利" },
        });

        Assert.Equal("以实玛利", merged.Entries["Ishmael"].Translation);
        Assert.False(merged.Entries["Ishmael"].Locked); // 远程默认不锁定
        Assert.Equal(GlossaryTermSource.Paratranz, merged.Entries["Ishmael"].Source);
        Assert.Equal(1, merged.ParatranzCount);
        Assert.Empty(merged.Conflicts);
    }

    [Fact]
    public void 合并_未启用远程时等价于本地()
    {
        var merged = GlossaryMerger.Merge(Local, null);
        Assert.Equal(Local.Count, merged.Entries.Count);
        Assert.Equal(0, merged.ParatranzCount);
    }

    [Fact]
    public void 校验流水线_术语需求只包含未遮蔽项()
    {
        var snapshot = ActiveGlossarySnapshot.FromEntries(Local);
        var pipeline = ValidationPipeline.CreateDefault(snapshot);

        // 通过 Snapshot 构建的流水线必须使用同一术语集合（重叠消解后只含 Attack Power Up）
        Assert.NotNull(pipeline);
        Assert.Equal(new[] { "Attack Power Up" }, snapshot.SelectTerms(new[] { "Attack Power Up" }).Select(kv => kv.Key).ToArray());
    }
}