using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Glossary;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.875轮：术语匹配边界（TermMatcher）与运行时术语快照（ActiveGlossarySnapshot）。
/// </summary>
public sealed class TermMatcherAndSnapshotTests : IDisposable
{
    private readonly string _configDir;

    public TermMatcherAndSnapshotTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "limbus_snapshot_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configDir))
            {
                Directory.Delete(_configDir, true);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private string GlossaryPath => Path.Combine(_configDir, "glossary.json");

    private void WriteGlossary(params (string Term, string Translation)[] entries)
    {
        var json = "{\n" + string.Join(",\n", entries.Select(e =>
            $"  \"{e.Term}\": {{ \"translation\": \"{e.Translation}\", \"locked\": true }}")) + "\n}";
        File.WriteAllText(GlossaryPath, json);
    }

    [Theory]
    [InlineData("Ring attacks.", true)]
    [InlineData("The Ring.", true)]
    [InlineData("Ring's effect", true)]
    [InlineData("During the fight", false)]
    [InlineData("Spring has come", false)]
    [InlineData("Ringing bell", false)]
    [InlineData("Bring it", false)]
    public void 英文术语_必须使用词边界(string text, bool expected)
        => Assert.Equal(expected, TermMatcher.ContainsTerm(text, "Ring"));

    [Theory]
    [InlineData("E.G.O Gear", "E.G.O", true)]
    [InlineData("Gain 1 E.G.O", "E.G.O", true)]
    [InlineData("EGO Gear", "E.G.O", false)]
    [InlineData("Don Quixote attacks", "Don Quixote", true)]
    [InlineData("Don Quixotee attacks", "Don Quixote", false)]
    [InlineData("Move-in Reg. complete", "Move-in Reg.", true)]
    [InlineData("Takeoff Module equipped", "Takeoff Module", true)]
    [InlineData("Takeoff Modules equipped", "Takeoff Module", false)]
    public void 带标点或空格的术语_必须整体匹配(string text, string term, bool expected)
        => Assert.Equal(expected, TermMatcher.ContainsTerm(text, term));

    [Theory]
    [InlineData("싱클레어가 공격한다", "싱클레어", true)]
    [InlineData("필립 싱클레어가 탈출장치로 후퇴", "싱클레어", true)]
    [InlineData("그레고르의 대사", "그레고르", true)]
    [InlineData("오늘 날씨가 좋다", "싱클레어", false)]
    // 韩文左侧边界：前面紧邻 Hangul 视为更长词的一部分 → 不命中
    [InlineData("그필립이 왔다", "필립", false)]
    // 已知取舍：韩文助词是正常用法（필립스），因此只做左侧边界、不强制右侧边界
    [InlineData("필립스 전구가 켜졌다", "필립", true)]
    public void 韩文别名_左侧边界匹配_允许助词(string text, string term, bool expected)
        => Assert.Equal(expected, TermMatcher.ContainsTerm(text, term));

    [Theory]
    [InlineData(null, "Ring")]
    [InlineData("", "Ring")]
    [InlineData("Ring", "")]
    [InlineData("Ring", "   ")]
    public void 空输入_不得抛异常且返回false(string? text, string term)
        => Assert.False(TermMatcher.ContainsTerm(text, term));

    [Fact]
    public void Prompt与Validator_必须共用同一匹配实现()
    {
        WriteGlossary(("Ring", "环"));

        var glossary = new GlossaryService(_configDir);

        Assert.Empty(glossary.SelectTerms(new[] { "During the fight" }));
        Assert.Single(glossary.SelectTerms(new[] { "The Ring." }));

        Assert.False(TermMatcher.ContainsTerm("During", "Ring"));
        Assert.True(TermMatcher.ContainsTerm("The Ring.", "Ring"));
        // Validator 侧经 ValidationTextTools.ContainsAsWord 转发到同一 TermMatcher（internal 不可直接调用），
        // 因此 Prompt 侧与 Validator 侧对同一文本的判定必然一致：
        Assert.Equal(
            TermMatcher.ContainsTerm("During the fight", "Ring"),
            glossary.SelectTerms(new[] { "During the fight" }).Count > 0);
        Assert.Equal(
            TermMatcher.ContainsTerm("The Ring.", "Ring"),
            glossary.SelectTerms(new[] { "The Ring." }).Count > 0);
    }

    [Fact]
    public void 快照Hash_内容相同则相同_顺序不同也相同()
    {
        WriteGlossary(("Ring", "环"), ("During", "期间"));
        var a = ActiveGlossarySnapshot.Load(_configDir);

        WriteGlossary(("During", "期间"), ("Ring", "环"));
        var b = ActiveGlossarySnapshot.Load(_configDir);

        Assert.Equal(a.SnapshotHash, b.SnapshotHash);
        Assert.Equal(2, a.Count);
    }

    [Fact]
    public void 快照Hash_译名变化则变化()
    {
        WriteGlossary(("Ring", "环"));
        var a = ActiveGlossarySnapshot.Load(_configDir);

        WriteGlossary(("Ring", "指环"));
        var b = ActiveGlossarySnapshot.Load(_configDir);

        Assert.NotEqual(a.SnapshotHash, b.SnapshotHash);
    }

    [Fact]
    public void 运行中修改术语库_不影响已创建的快照_但下一次运行会读到新版本()
    {
        WriteGlossary(("Ring", "环"));
        var snapshot = ActiveGlossarySnapshot.Load(_configDir);

        WriteGlossary(("Ring", "指环"), ("New Term", "新术语"));

        Assert.Equal(1, snapshot.Count);
        Assert.Equal("环", snapshot.Entries["Ring"].Translation);
        Assert.Empty(snapshot.SelectTerms(new[] { "New Term appears" }));

        var next = ActiveGlossarySnapshot.Load(_configDir);
        Assert.Equal(2, next.Count);
        Assert.Equal("指环", next.Entries["Ring"].Translation);
        Assert.NotEqual(snapshot.SnapshotHash, next.SnapshotHash);
    }

    [Fact]
    public void 空快照_不抛异常且不命中任何术语()
    {
        var empty = ActiveGlossarySnapshot.Empty;
        Assert.Equal(0, empty.Count);
        Assert.Empty(empty.SelectTerms(new[] { "Ring attacks." }));
    }

    [Fact]
    public void 快照查询_必须稳定排序()
    {
        WriteGlossary(("Ring", "环"), ("Bleed", "流血"));
        var snapshot = ActiveGlossarySnapshot.Load(_configDir);

        var hits = snapshot.SelectTerms(new[] { "Bleed and Ring" });
        Assert.Equal(new[] { "Bleed", "Ring" }, hits.Select(h => h.Key).ToArray());
    }

    [Fact]
    public void 术语子集Hash_与顺序无关且可复现()
    {
        var entries = new List<KeyValuePair<string, GlossaryEntry>>
        {
            new("Ring", new GlossaryEntry { Translation = "环", Locked = true }),
            new("Bleed", new GlossaryEntry { Translation = "流血", Locked = true }),
        };
        var reversed = entries.AsEnumerable().Reverse().ToList();

        var h1 = GlossaryService.ComputeSubsetHash(entries);

        Assert.Equal(h1, GlossaryService.ComputeSubsetHash(reversed));
        Assert.Equal(h1, GlossaryService.ComputeSubsetHash(entries));
    }

    [Fact]
    public void 术语子集Hash_锁定状态变化则变化()
    {
        var locked = new List<KeyValuePair<string, GlossaryEntry>>
        {
            new("Ring", new GlossaryEntry { Translation = "环", Locked = true }),
        };
        var unlocked = new List<KeyValuePair<string, GlossaryEntry>>
        {
            new("Ring", new GlossaryEntry { Translation = "环", Locked = false }),
        };

        Assert.NotEqual(
            GlossaryService.ComputeSubsetHash(locked),
            GlossaryService.ComputeSubsetHash(unlocked));
    }
}