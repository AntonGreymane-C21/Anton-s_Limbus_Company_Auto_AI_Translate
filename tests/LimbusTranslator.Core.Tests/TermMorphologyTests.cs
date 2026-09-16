using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Validation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.4轮：**英文术语受控词形匹配**回归。
///
/// 真实缺陷：术语库写 <c>Nursefather</c>，英文原文写 <c>Nursefathers</c>，
/// 旧规则因为"术语后面还有字母"而完全不命中 ⇒ MatchedTerms 为空 ⇒ Prompt 没有该锁定术语
/// ⇒ Validator 也看不到它 ⇒ 自动修正自然不触发（用户实测：译成"护理之父们"且无任何告警）。
/// </summary>
public sealed class TermMorphologyTests
{
    private static ActiveGlossarySnapshot Glossary(params (string Term, string Translation, bool Locked)[] terms)
    {
        var entries = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (term, translation, locked) in terms)
        {
            entries[term] = new GlossaryEntry { Translation = translation, Locked = locked };
        }

        return ActiveGlossarySnapshot.FromEntries(entries, sourcePath: null);
    }

    private static IReadOnlyList<TerminologyRequirement> Requirements(ActiveGlossarySnapshot snapshot)
        => snapshot.Entries
            .Select(kv => new TerminologyRequirement
            {
                Source = kv.Key,
                Target = kv.Value.Translation,
                Locked = kv.Value.Locked,
            })
            .ToList();

    // ───────── §十五 / §十六 真实例子：Nursefathers ─────────

    [Fact]
    public void 英文锁定术语_基础形式应匹配复数形式()
    {
        const string source = "The Nursefathers who have died, those who survived, and the rest of you who've made it all the way here...";
        var snapshot = Glossary(("Nursefather", "护父", true));

        var matched = snapshot.SelectTerms(new[] { source });

        var hit = Assert.Single(matched);
        Assert.Equal("Nursefather", hit.Key);
        Assert.Equal("护父", hit.Value.Translation);
        Assert.True(hit.Value.Locked);
    }

    [Fact]
    public void 英文锁定术语_复数形式也必须被违规检出()
    {
        // 这一半同样关键：Validator 用的是同一个 Matcher，旧规则下它连"违规"都发现不了。
        const string source = "The Nursefathers who have died.";
        var terms = Requirements(Glossary(("Nursefather", "护父", true)));

        var violations = LockedTerminologyCheck.FindViolations(source, "死去的护理之父们", terms, requireWordBoundary: true);

        var violation = Assert.Single(violations);
        Assert.Equal("Nursefather", violation.Source);
        Assert.Equal("护父", violation.Target);

        // 译文里出现规定译法 ⇒ 不再违规
        Assert.Empty(LockedTerminologyCheck.FindViolations(source, "死去的护父们", terms, requireWordBoundary: true));
    }

    // ───────── §十七 所有格 / §十九 多词术语 ─────────

    [Theory]
    [InlineData("Nursefather")]
    [InlineData("Nursefathers")]
    [InlineData("Nursefather's")]
    [InlineData("Nursefathers'")]
    public void 英文术语_原形复数与所有格都命中同一条基础术语(string surface)
    {
        var snapshot = Glossary(("Nursefather", "护父", true));

        var matched = snapshot.SelectTerms(new[] { $"Text {surface} text." });

        var hit = Assert.Single(matched);
        Assert.Equal("Nursefather", hit.Key);
    }

    [Theory]
    [InlineData("Mirror Dungeon")]
    [InlineData("Mirror Dungeons")]
    [InlineData("Mirror Dungeon's")]
    [InlineData("Mirror Dungeons'")]
    public void 英文术语_多词术语的复数与所有格命中基础术语(string surface)
    {
        var snapshot = Glossary(("Mirror Dungeon", "镜面迷宫", true));

        var matched = snapshot.SelectTerms(new[] { $"Enter the {surface} now." });

        var hit = Assert.Single(matched);
        Assert.Equal("Mirror Dungeon", hit.Key);
        Assert.Equal("镜面迷宫", hit.Value.Translation);
    }

    // ───────── §九 / §十八 负例（防误匹配）─────────

    [Theory]
    [InlineData("Mark")]
    [InlineData("Marks")]
    [InlineData("Mark's")]
    [InlineData("Marks'")]
    public void 英文术语_允许的形式命中(string surface)
    {
        var snapshot = Glossary(("Mark", "标记", true));

        Assert.Single(snapshot.SelectTerms(new[] { $"A {surface} here." }));
    }

    [Theory]
    [InlineData("Marked")]
    [InlineData("Marker")]
    [InlineData("Markets")]
    [InlineData("Landmark")]
    [InlineData("Ringing")]
    [InlineData("During")]
    public void 英文术语_词内与派生词不得命中(string word)
    {
        var snapshot = Glossary(("Mark", "标记", true));

        Assert.Empty(snapshot.SelectTerms(new[] { $"A {word} here." }));
    }

    // ───────── §十一 / §十二 Longest Match Wins 与复合词 ─────────

    [Fact]
    public void 英文术语_最长术语优先且不拆分复合词()
    {
        var snapshot = Glossary(
            ("Nursefather", "护父", true),
            ("Father", "父亲", true),
            ("Nurse", "护士", false));

        var matched = snapshot.SelectTerms(new[] { "The Nursefathers arrived." });

        var hit = Assert.Single(matched);
        Assert.Equal("Nursefather", hit.Key);          // 只有最长者胜出
        Assert.DoesNotContain(matched, kv => kv.Key == "Nurse");
        Assert.DoesNotContain(matched, kv => kv.Key == "Father");
    }

    [Fact]
    public void 英文术语_独立出现时仍然各自命中()
    {
        var snapshot = Glossary(("Nursefather", "护父", true), ("Father", "父亲", true));

        var matched = snapshot.SelectTerms(new[] { "The Father and the Nursefathers." });

        Assert.Equal(2, matched.Count);
        Assert.Contains(matched, kv => kv.Key == "Father");
        Assert.Contains(matched, kv => kv.Key == "Nursefather");
    }

    // ───────── §十 大小写语义保持（既有 IgnoreCase）─────────

    [Theory]
    [InlineData("nursefather")]
    [InlineData("NURSEFATHER")]
    [InlineData("NurseFATHERS")]
    public void 英文术语_大小写不敏感语义保持(string surface)
    {
        var snapshot = Glossary(("Nursefather", "护父", true));

        Assert.Single(snapshot.SelectTerms(new[] { $"The {surface} came." }));
    }

    // ───────── §三十八 / §三十九 命中的表面形式（诊断）─────────

    [Fact]
    public void 英文术语_可看到实际命中的表面形式()
    {
        var matches = TermMatcher.FindAll(
            "The Nursefathers arrived.",
            new[] { ("Nursefather", "护父", true) });

        var match = Assert.Single(matches);
        Assert.Equal("Nursefather", match.Term);
        Assert.Equal("Nursefathers", match.MatchedSurface);
    }

    // ───────── §三十六 韩文术语不受英文词形规则影响 ─────────

    [Fact]
    public void 韩文术语_仍按左侧边界匹配且不套英文词形规则()
    {
        var snapshot = Glossary(("너스파더", "护父", true));

        // 韩文助词照常允许（既有语义）
        Assert.Single(snapshot.SelectTerms(new[] { "핑키 너스파더가 나타났다." }));

        // 英文锁定术语不得命中韩文文本（英文词形规则不外溢）
        var english = Glossary(("Nursefather", "护父", true));
        Assert.Empty(english.SelectTerms(new[] { "핑키 너스파더가 나타났다." }));
    }
}
