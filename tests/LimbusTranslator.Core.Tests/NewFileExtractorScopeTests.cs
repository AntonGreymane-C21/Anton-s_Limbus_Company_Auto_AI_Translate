using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Services;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.22轮：**按勾选提取**（`NewFileExtractor` 的范围过滤）。
///
/// 背景：旧实现无视勾选，全量提取"新增/缺失/修改"的文件 ⇒ UI 勾 5 个、提取 100 个。
/// 现在 WPF 会传入当前勾选的文件；范围判定用**逻辑路径**（与任务选择同口径，不需要 EN_ 前缀转换）。
/// </summary>
public sealed class NewFileExtractorScopeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "LT_Extract_" + Guid.NewGuid().ToString("N"));

    public NewFileExtractorScopeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "en", "StoryData"));
        File.WriteAllText(Path.Combine(_root, "en", "StoryData", "EN_A.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "en", "StoryData", "EN_B.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "en", "EN_Root.json"), "{}");
    }

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

    private string EnglishRoot => Path.Combine(_root, "en");

    private string TargetRoot => Path.Combine(_root, "pending");

    private static FileDiffEntry Entry(string logical, string english, FileDiffKind kind = FileDiffKind.Modified)
        => new() { LogicalPath = logical, EnglishPath = english, Kind = kind };

    private List<FileDiffEntry> Entries() => new()
    {
        Entry("StoryData/A.json", "StoryData/EN_A.json"),
        Entry("StoryData/B.json", "StoryData/EN_B.json"),
        Entry("Root.json", "EN_Root.json"),
    };

    [Fact]
    public void 不传范围时保持旧行为_全量提取()
    {
        var result = NewFileExtractor.Extract(EnglishRoot, Entries(), TargetRoot);

        Assert.Equal(3, result.CopiedCount);
        Assert.True(File.Exists(Path.Combine(TargetRoot, "StoryData", "EN_A.json")));
        Assert.True(File.Exists(Path.Combine(TargetRoot, "EN_Root.json")));
    }

    [Fact]
    public void 传范围时只提取集合内的文件()
    {
        var result = NewFileExtractor.Extract(
            EnglishRoot, Entries(), TargetRoot, kinds: null, restrictToRelativePaths: new[] { "StoryData/A.json" });

        Assert.Equal(1, result.CopiedCount);
        Assert.Equal(new[] { "StoryData/EN_A.json" }, result.Files.ToArray());          // 物理名（供展示）
        Assert.Equal(new[] { "StoryData/A.json" }, result.LogicalFiles.ToArray());      // 逻辑名（供收敛/清单）

        Assert.True(File.Exists(Path.Combine(TargetRoot, "StoryData", "EN_A.json")));
        Assert.False(File.Exists(Path.Combine(TargetRoot, "StoryData", "EN_B.json")));
        Assert.False(File.Exists(Path.Combine(TargetRoot, "EN_Root.json")));
    }

    [Fact]
    public void 范围判定使用逻辑路径_物理名不会误命中()
    {
        var result = NewFileExtractor.Extract(
            EnglishRoot, Entries(), TargetRoot,
            kinds: null,
            restrictToRelativePaths: new[] { "StoryData/EN_A.json" });   // 故意用物理名

        Assert.Equal(0, result.CopiedCount);
    }

    [Fact]
    public void 范围为空集合时什么都不提取()
    {
        var result = NewFileExtractor.Extract(
            EnglishRoot, Entries(), TargetRoot, kinds: null, restrictToRelativePaths: Array.Empty<string>());

        Assert.Equal(0, result.CopiedCount);
    }

    [Fact]
    public void 范围包含不存在的文件时不报错()
    {
        var result = NewFileExtractor.Extract(
            EnglishRoot, Entries(), TargetRoot,
            kinds: null,
            restrictToRelativePaths: new[] { "StoryData/A.json", "StoryData/不存在.json" });

        Assert.Equal(1, result.CopiedCount);
    }

    [Fact]
    public void 范围过滤与类型过滤叠加()
    {
        var entries = new List<FileDiffEntry>
        {
            Entry("StoryData/A.json", "StoryData/EN_A.json", FileDiffKind.New),
            Entry("StoryData/B.json", "StoryData/EN_B.json", FileDiffKind.Unchanged),
        };

        var result = NewFileExtractor.Extract(
            EnglishRoot, entries, TargetRoot,
            kinds: new[] { FileDiffKind.New },
            restrictToRelativePaths: new[] { "StoryData/A.json", "StoryData/B.json" });

        Assert.Equal(1, result.CopiedCount);
        Assert.Equal("StoryData/EN_A.json", result.Files[0]);
    }
}
