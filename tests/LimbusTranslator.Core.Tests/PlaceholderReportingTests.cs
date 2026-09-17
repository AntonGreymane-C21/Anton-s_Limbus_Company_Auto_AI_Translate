using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Placeholder;
using LimbusTranslator.Infrastructure.Validation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.14轮：**占位符/标签报告的"说人话"与去重** + `RestoreLenient` 就位修复。
///
/// 背景（用户实测的 523 条 `__LT_PH_…` 报错）：
///   - 真实成因：EN/KR 源文含 <c>&lt;i&gt;</c> 等标记，而旧汉化没有 ⇒ 保护器把源文标记编号后比较，
///     报告里就出现了内部标记名「缺失 __LT_PH_0001__,__LT_PH_0002__」——对人类毫无意义；
///   - 且同一个标签丢失被「占位符校验」与「标签校验」各报一次 ⇒ 历史继承问题统计翻倍。
/// </summary>
public sealed class PlaceholderReportingTests
{
    private static ValidationContext Context(string source, string translation)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "StoryData/X.json", RecordId = "1", FieldPath = "dataList[0].content" },
            SourceText = source,
            Translation = translation,
        };

    [Fact]
    public void 占位符报告必须显示真实占位符而不是内部标记()
    {
        var issues = new PlaceholderValidator().Validate(Context("{0} HP remaining", "剩余生命值"));

        var issue = Assert.Single(issues);
        Assert.Equal(ValidationIssueCodes.PlaceholderMismatch, issue.Code);
        Assert.Contains("{0}", issue.Message);
        Assert.DoesNotContain("__LT_PH_", issue.Message);
    }

    [Fact]
    public void 标签缺失不再重复计为占位符问题()
    {
        var context = Context("<i>Hatch</i>? You are joking.", "哈奇？你在开玩笑吧。");

        // 占位符校验：标签已交给 TagValidator ⇒ 这里不再报（避免同一个问题报两次）
        Assert.Empty(new PlaceholderValidator().Validate(context));

        // 标签校验：仍然如实报出（结构安全没有被放宽）
        var tagIssues = new TagValidator().Validate(context);
        Assert.Contains(tagIssues, issue => issue.Code == ValidationIssueCodes.TagMismatch);
    }

    [Fact]
    public void 宽容恢复_起始标签补到句首_结束标签补到句尾()
    {
        var protector = new PlaceholderProtector();
        var source = protector.Protect("<i>Hatch</i>? You are joking.");

        // 模型把两个标签都丢了
        var (text, validation) = protector.RestoreLenient("哈奇？你在开玩笑吧。", source);

        Assert.False(validation.IsValid);
        Assert.Equal("<i>哈奇？你在开玩笑吧。</i>", text);
    }

    [Fact]
    public void 宽容恢复_数字占位符仍补到句尾且不丢数据()
    {
        var protector = new PlaceholderProtector();
        var source = protector.Protect("{0} HP remaining");

        var (text, validation) = protector.RestoreLenient("剩余生命值", source);

        Assert.False(validation.IsValid);
        Assert.Equal("剩余生命值{0}", text);   // 位置无法推断，但绝不丢数据
    }

    [Fact]
    public void 占位符描述_同一占位符多次缺失时带次数()
    {
        var protector = new PlaceholderProtector();
        var source = protector.Protect("<i>A</i> and <i>B</i>");

        var validation = protector.Validate("甲和乙", source);
        var described = validation.Describe(source);

        Assert.Contains("缺失", described);
        Assert.Contains("<i>×2", described);
        Assert.Contains("</i>×2", described);
        Assert.DoesNotContain("__LT_PH_", described);
    }
}
