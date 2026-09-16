using System.Text.Json;
using LimbusTranslator.Infrastructure.DeepSeek;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第2轮：DeepSeek 协议层响应解析与 ID 集合校验测试（Missing / Extra / Duplicate）。
/// 纯字符串测试，不访问网络。
/// </summary>
public class DeepSeekResponseParserTests
{
    private static List<DeepSeekTranslateRequestItem> Request(params string[] ids)
        => ids.Select(id => new DeepSeekTranslateRequestItem { Id = id, Source = "source" }).ToList();

    private static string Item(string id, string translation = "译文")
        => $"{{\"id\":\"{DeepSeekResponseParser.EncodeId(id)}\",\"translation\":\"{translation}\",\"needs_review\":false,\"reason\":\"\"}}";

    private static string Response(params string[] items)
    {
        var inner = "{\"items\":[" + string.Join(",", items) + "]}";
        return "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize(inner) + "}}]}";
    }

    [Fact]
    public void 正常完整ID_解析通过()
    {
        var request = Request("A", "B");
        var results = DeepSeekResponseParser.Parse(Response(Item("A", "甲"), Item("B", "乙")), request);

        Assert.Equal(2, results.Count);
        Assert.Equal("甲", results["A"].Translation);
        Assert.Equal("乙", results["B"].Translation);
    }

    [Fact]
    public void 缺少ID_被拒绝()
    {
        var request = Request("A", "B");

        var ex = Assert.Throws<JsonException>(() => DeepSeekResponseParser.Parse(Response(Item("A")), request));
        Assert.Contains("不一致", ex.Message);

        Assert.Throws<JsonException>(() => DeepSeekResponseParser.Parse(Response(Item("A"), Item("C")), request));
    }

    [Fact]
    public void 额外ID_被拒绝()
    {
        var request = Request("A");

        Assert.Throws<JsonException>(() => DeepSeekResponseParser.Parse(Response(Item("A"), Item("B")), request));
    }

    [Fact]
    public void 重复ID_被明确拒绝()
    {
        // 旧实现：A,A,B 写入字典后只剩 {A,B}，数量与请求一致 → 漏检！
        // 新实现：必须在写入字典之前显式检测重复 id。
        var request = Request("A", "B");

        var ex = Assert.Throws<JsonException>(() => DeepSeekResponseParser.Parse(Response(Item("A"), Item("A"), Item("B")), request));
        Assert.Contains("重复 id", ex.Message);
    }

    [Fact]
    public void 缺少id字段_被拒绝()
    {
        var request = Request("A");
        var broken = "{\"choices\":[{\"message\":{\"content\":" + JsonSerializer.Serialize("{\"items\":[{\"translation\":\"译文\"}]}") + "}}]}";

        Assert.Throws<JsonException>(() => DeepSeekResponseParser.Parse(broken, request));
    }

    [Fact]
    public void Id编解码_可往返()
    {
        const string key = "StoryData/EN_1D101A.json|1001|dataList[3].content";

        Assert.Equal(key, DeepSeekResponseParser.DecodeId(DeepSeekResponseParser.EncodeId(key)));
    }
}
