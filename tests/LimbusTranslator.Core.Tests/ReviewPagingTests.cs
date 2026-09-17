using LimbusTranslator.Infrastructure.Presentation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.12轮：**待审核列表分页** 回归（纯函数层）。
///
/// 背景（用户实测）：上一轮把待审核列表硬截断为前 5000 条 ⇒ 排在第 5000 条之后的**整文件**
/// 在待审核页完全看不见（StoryData/S1000B.json 等十余个文件）；而且真正的待审条目也可能被截掉。
/// 现在改为「完整集合 + 每页 5000 条分页」，本文件固化分页数学与"跳到文件"的定位规则。
/// </summary>
public sealed class ReviewPagingTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(5000, 1)]
    [InlineData(5001, 2)]
    [InlineData(10000, 2)]
    [InlineData(10001, 3)]
    public void 页数计算_边界正确(int total, int expectedPages)
        => Assert.Equal(expectedPages, ReviewPaging.PageCount(total, ReviewPaging.DefaultPageSize));

    [Theory]
    [InlineData(0, 5001, 1)]
    [InlineData(-5, 5001, 1)]
    [InlineData(1, 5001, 1)]
    [InlineData(2, 5001, 2)]
    [InlineData(99, 5001, 2)]
    public void 页码收敛_越界自动夹取(int input, int total, int expected)
        => Assert.Equal(expected, ReviewPaging.ClampPage(input, total, ReviewPaging.DefaultPageSize));

    [Fact]
    public void 取页_返回正确的切片()
    {
        var all = Enumerable.Range(1, 12000).ToList();

        var page1 = ReviewPaging.GetPage(all, 1, ReviewPaging.DefaultPageSize);
        var page2 = ReviewPaging.GetPage(all, 2, ReviewPaging.DefaultPageSize);
        var page3 = ReviewPaging.GetPage(all, 3, ReviewPaging.DefaultPageSize);

        Assert.Equal(5000, page1.Count);
        Assert.Equal(1, page1[0]);
        Assert.Equal(5000, page1[^1]);

        Assert.Equal(5000, page2.Count);
        Assert.Equal(5001, page2[0]);       // 第 2 页从第 5001 条开始（旧实现正是这里被截断）
        Assert.Equal(10000, page2[^1]);

        Assert.Equal(2000, page3.Count);
        Assert.Equal(10001, page3[0]);
    }

    [Fact]
    public void 取页_空集合返回空_超界页码收敛到有效页()
    {
        Assert.Empty(ReviewPaging.GetPage(Array.Empty<int>(), 1, ReviewPaging.DefaultPageSize));

        // 页码越界一律按 ClampPage 收敛（第 5 页 → 第 1 页），所以返回全部 2 条而不是空。
        // 这样"翻页到最后一页后又删除条目"之类的边界不会让列表变空。
        Assert.Equal(new[] { 1, 2 }, ReviewPaging.GetPage(new List<int> { 1, 2 }, 5, ReviewPaging.DefaultPageSize));
    }

    [Fact]
    public void 跳到文件_能找到第5000条之后的文件所在页()
    {
        // 造 12000 条：头部全是 A 文件，S1000B 从第 5001 条开始
        var files = new List<string>();
        files.AddRange(Enumerable.Repeat("StoryData/A.json", 5000));
        files.AddRange(Enumerable.Repeat("StoryData/S1000B.json", 7000));

        Assert.Equal(2, ReviewPaging.FindPageForFile(files, "S1000B", ReviewPaging.DefaultPageSize));
        Assert.Equal(1, ReviewPaging.FindPageForFile(files, "A.json", ReviewPaging.DefaultPageSize));
    }

    [Theory]
    [InlineData("s1000b")]      // 忽略大小写
    [InlineData("1000")]        // 部分匹配
    public void 跳到文件_匹配规则宽容(string fragment)
        => Assert.Equal(2, ReviewPaging.FindPageForFile(
            Enumerable.Repeat("X.json", 5000).Concat(Enumerable.Repeat("StoryData/S1000B.json", 1)).ToList(),
            fragment,
            ReviewPaging.DefaultPageSize));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DoesNotExist")]
    public void 跳到文件_无效输入返回零(string fragment)
        => Assert.Equal(0, ReviewPaging.FindPageForFile(
            Enumerable.Repeat("StoryData/A.json", 6000).ToList(),
            fragment,
            ReviewPaging.DefaultPageSize));
}
