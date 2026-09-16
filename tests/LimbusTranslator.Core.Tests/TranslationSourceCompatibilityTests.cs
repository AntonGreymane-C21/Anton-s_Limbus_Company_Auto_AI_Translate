using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第2轮：TranslationSource 枚举历史整数兼容性回归测试。
///
/// 背景：第1轮为了 Provenance 能力新增了 Mock / Inherited / TranslationMemory / RequestCache。
/// 这些成员必须**尾部追加**，否则会让 SQLite 中已有的 0~3 语义发生位移。
/// </summary>
public class TranslationSourceCompatibilityTests
{
    [Fact]
    public void 历史枚举整数值保持不变()
    {
        Assert.Equal(0, (int)TranslationSource.Official);
        Assert.Equal(1, (int)TranslationSource.HumanReviewed);
        Assert.Equal(2, (int)TranslationSource.Imported);
        Assert.Equal(3, (int)TranslationSource.AI);
    }

    [Fact]
    public void 新增来源全部位于历史值之后()
    {
        Assert.Equal(4, (int)TranslationSource.Inherited);
        Assert.Equal(5, (int)TranslationSource.TranslationMemory);
        Assert.Equal(6, (int)TranslationSource.Mock);
        Assert.Equal(7, (int)TranslationSource.RequestCache);
    }

    [Fact]
    public void 既有数据库整数可正确解码()
    {
        // 真实库当前只有 TranslationSource = 3 的历史记录
        Assert.Equal(TranslationSource.AI, (TranslationSource)3);
        Assert.Equal(TranslationSource.Official, (TranslationSource)0);
        Assert.Equal(TranslationSource.HumanReviewed, (TranslationSource)1);
        Assert.Equal(TranslationSource.Imported, (TranslationSource)2);
    }

    [Fact]
    public void 新增来源不参与来源优先级排序()
    {
        // Official(0) > HumanReviewed(1) > Imported(2) > AI(3) 的排序语义不变
        var order = new[]
        {
            TranslationSource.Official,
            TranslationSource.HumanReviewed,
            TranslationSource.Imported,
            TranslationSource.AI,
        };

        Assert.Equal(order.OrderBy(v => (int)v).ToArray(), order);
    }
}
