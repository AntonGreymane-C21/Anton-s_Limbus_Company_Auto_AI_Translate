using LimbusTranslator.Infrastructure.Services;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.21轮（R6）：**输出工作区卫生** —— `data/output` 下的非权威产物（如 P6 冒烟写的
/// <c>real_api_smoke/</c>）既不应进入清单/部署，也不应出现在"即将复制的文件数"里。
/// </summary>
public sealed class OutputWorkspaceHygieneTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "LT_OutputHygiene_" + Guid.NewGuid().ToString("N"));

    public OutputWorkspaceHygieneTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // 清理失败可忽略
        }
    }

    [Theory]
    [InlineData("real_api_smoke/StoryData/x.json")]
    [InlineData("real_api_smoke\\x.json")]
    [InlineData("REAL_API_SMOKE/x.json")]
    [InlineData("real_api_smoke")]
    public void 非权威产物路径必须被识别(string relativePath)
        => Assert.True(OutputWorkspaceHygiene.IsNonAuthoritative(relativePath));

    [Theory]
    [InlineData("StoryData/S1015B.json")]
    [InlineData("real_api_smoke2/x.json")]
    [InlineData("real_api_smoke_notes.json")]
    [InlineData("")]
    [InlineData(null)]
    public void 正常输出路径不得被误判(string? relativePath)
        => Assert.False(OutputWorkspaceHygiene.IsNonAuthoritative(relativePath));

    [Fact]
    public void 清理只删除非权威产物_保留正常输出文件()
    {
        var smokeDir = Path.Combine(_root, "real_api_smoke", "StoryData");
        Directory.CreateDirectory(smokeDir);
        File.WriteAllText(Path.Combine(smokeDir, "smoke.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "real_api_smoke", "summary.json"), "{}");

        var storyDir = Path.Combine(_root, "StoryData");
        Directory.CreateDirectory(storyDir);
        File.WriteAllText(Path.Combine(storyDir, "S1015B.json"), "{}");

        var logs = new List<string>();
        var removed = OutputWorkspaceHygiene.Cleanup(_root, logs.Add);

        Assert.Equal(2, removed);
        Assert.False(Directory.Exists(Path.Combine(_root, "real_api_smoke")));
        Assert.True(File.Exists(Path.Combine(storyDir, "S1015B.json")));   // 正常输出未受影响
        Assert.Contains(logs, line => line.Contains("已删除非权威产物目录"));
    }

    [Fact]
    public void 目录不存在时清理返回零且不抛异常()
    {
        Assert.Equal(0, OutputWorkspaceHygiene.Cleanup(Path.Combine(_root, "不存在的目录")));
        Assert.Equal(0, OutputWorkspaceHygiene.Cleanup(string.Empty));
    }
}
