using LimbusTranslator.Infrastructure.Placeholder;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// Placeholder 保护/恢复/校验测试（文档 §29/§30）。
/// </summary>
public class PlaceholderProtectorTests
{
    private readonly PlaceholderProtector _protector = new();

    [Fact]
    public void 数字占位符_保护并恢复()
    {
        var source = "Inflict {0} Bleed.";
        var protectedText = _protector.Protect(source);

        Assert.Equal("Inflict __LT_PH_0001__ Bleed.", protectedText.ProtectedText);
        Assert.Single(protectedText.OriginalPlaceholders);
        Assert.Equal("{0}", protectedText.OriginalPlaceholders[0]);

        var restored = _protector.Restore("施加__LT_PH_0001__层流血。", protectedText);
        Assert.Equal("施加{0}层流血。", restored);
    }

    [Fact]
    public void 富文本标签_保护并恢复()
    {
        var source = "<color=red>Increases</color> by {1}%.";
        var protectedText = _protector.Protect(source);

        Assert.Equal("__LT_PH_0001__Increases__LT_PH_0002__ by __LT_PH_0003__%.",
            protectedText.ProtectedText);
        Assert.Equal(3, protectedText.OriginalPlaceholders.Count);
        Assert.Equal("<color=red>", protectedText.OriginalPlaceholders[0]);
        Assert.Equal("</color>", protectedText.OriginalPlaceholders[1]);
        Assert.Equal("{1}", protectedText.OriginalPlaceholders[2]);

        var restored = _protector.Restore(
            "__LT_PH_0001__增加__LT_PH_0002__ __LT_PH_0003__%。",
            protectedText);
        Assert.Equal("<color=red>增加</color> {1}%。", restored);
    }

    [Fact]
    public void 换行与方括号_保护()
    {
        var source = "Line1\\n[Gregor] Line2";
        var protectedText = _protector.Protect(source);

        Assert.Contains("__LT_PH_", protectedText.ProtectedText);
        Assert.DoesNotContain("\\n", protectedText.ProtectedText);
        Assert.DoesNotContain("Gregor", protectedText.ProtectedText);
    }

    [Fact]
    public void 校验_缺失标记_应失败()
    {
        var source = "Gain {0} Haste.";
        var protectedText = _protector.Protect(source);

        // AI 把标记丢了
        var validation = _protector.Validate("获得一些迅捷。", protectedText);
        Assert.False(validation.IsValid);
        Assert.Single(validation.Missing);
        Assert.Equal("__LT_PH_0001__", validation.Missing[0]);
    }

    [Fact]
    public void 校验_重复标记_应失败()
    {
        var source = "Gain {0} {1} Haste.";
        var protectedText = _protector.Protect(source);

        // AI 重复了同一个标记
        var validation = _protector.Validate(
            "获得__LT_PH_0001__ __LT_PH_0001__迅捷。", protectedText);
        Assert.False(validation.IsValid);
        Assert.Single(validation.Duplicated);
        Assert.Equal("__LT_PH_0001__", validation.Duplicated[0]);
    }

    [Fact]
    public void 校验_未知标记_应失败()
    {
        var source = "Gain {0} Haste.";
        var protectedText = _protector.Protect(source);

        // AI 自己造了个标记
        var validation = _protector.Validate(
            "获得__LT_PH_0001__迅捷__LT_PH_9999__。", protectedText);
        Assert.False(validation.IsValid);
        Assert.Single(validation.Unknown);
        Assert.Equal("__LT_PH_9999__", validation.Unknown[0]);
    }

    [Fact]
    public void 校验_全部正确_应通过()
    {
        var source = "<color=red>{0}</color> {1}%.";
        var protectedText = _protector.Protect(source);

        var validation = _protector.Validate(
            "__LT_PH_0001____LT_PH_0002____LT_PH_0003__ __LT_PH_0004__%。",
            protectedText);
        Assert.True(validation.IsValid);
        Assert.Equal("通过", validation.Describe());
    }

    [Fact]
    public void 恢复_未通过校验_应抛异常()
    {
        var source = "Gain {0} Haste.";
        var protectedText = _protector.Protect(source);

        Assert.Throws<InvalidOperationException>(() =>
            _protector.Restore("获得一些迅捷。", protectedText));
    }
}
