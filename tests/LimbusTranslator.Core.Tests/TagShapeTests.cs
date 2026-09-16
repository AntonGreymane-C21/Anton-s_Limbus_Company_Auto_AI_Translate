using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Validation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.6轮：**富文本标签形态收严** 回归（修 TAG_MISMATCH 假报警）。
///
/// 真实故障：Limbus 剧情文本用同一套尖括号包裹台词强调，例如
///   &lt;I hereby confirm today's close of business for the Sinners.&gt; / &lt;araya …?&gt; / &lt;the …&gt; / &lt;that …&gt;
/// 旧的标签正则把它当成"标签"，抽出标签名 i / araya / the / that ⇒ 每次真实翻译都被判 TAG_MISMATCH（硬错误
/// ⇒ 发布门禁 Blocked），历史继承旧中文也因此累计 2766 条假报警。
/// </summary>
public sealed class TagShapeTests
{
    private static readonly UnitKey Key = new()
    {
        RelativeFilePath = "StoryData/S949A.json",
        RecordId = "4",
        FieldPath = "dataList[4].content",
    };

    private static IReadOnlyList<ValidationIssue> ValidateTags(string source, string translation)
        => new TagValidator().Validate(new ValidationContext
        {
            Key = Key,
            SourceText = source,
            Translation = translation,
        });

    private static string Describe(IReadOnlyList<ValidationIssue> issues)
        => string.Join("｜", issues.Select(issue => issue.Message));

    // ───────── 台词包裹（尖括号强调）绝不能被当成标签 ─────────

    [Theory]
    [InlineData("<I hereby confirm today's close of business for the Sinners.>")]
    [InlineData("<I still clearly remember their names.>")]
    [InlineData("<araya …?>")]
    [InlineData("<the last one>")]
    [InlineData("<that moment>")]
    [InlineData("<ryōshū …?>")]
    public void 尖括号台词包裹不得被当成标签(string source)
    {
        // 译文把包裹整体换成中文（真实场景：模型保留 <...> 但内容是中文）
        var translation = "<" + new string('中', 8) + ">";

        Assert.Empty(ValidateTags(source, translation));
    }

    // ───────── 真标签必须仍然被识别与保护 ─────────

    [Theory]
    [InlineData("<i>Hello</i>", "<i>你好</i>")]
    [InlineData("<b>Hello</b>", "<b>你好</b>")]
    [InlineData("<color=#ffffa1>Hello</color>", "<color=#FFFFFF>你好</color>")]  // 属性差异不算问题
    [InlineData("<size=80%>Hello</size>", "<size=80%>你好</size>")]
    [InlineData("<ruby=chariot>Hello</ruby>", "<ruby=战车>你好</ruby>")]
    public void 真标签保留时不得报警(string source, string translation)
        => Assert.Empty(ValidateTags(source, translation));

    [Fact]
    public void 真标签丢失必须仍然报硬错误()
    {
        var issues = ValidateTags("<i>Hello</i>", "你好");

        Assert.NotEmpty(issues);
        Assert.Contains("i×2", Describe(issues));
    }

    [Fact]
    public void 真标签与台词包裹混排时只比较真标签()
    {
        // 源文：台词包裹 + 真斜体标签；译文：中文包裹 + 保留斜体
        const string source = "<I still remember.> <i>Sen</i> and <i>Ryōshū</i>.";
        const string translation = "<我还记得。> <i>莲</i> 和 <i>良秀</i>。";

        Assert.Empty(ValidateTags(source, translation));
    }
}
