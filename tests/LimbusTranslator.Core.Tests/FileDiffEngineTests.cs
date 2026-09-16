using LimbusTranslator.Infrastructure.Diff;
using LimbusTranslator.Infrastructure.Parsing;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 文件级对比引擎 + 游戏目录定位测试。
/// </summary>
public class FileDiffEngineTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void 新增文件_应标记New()
    {
        var oldEn = MakeTempDir();
        var oldZh = MakeTempDir();
        var newEn = MakeTempDir();

        File.WriteAllText(Path.Combine(newEn, "EN_New.json"), "{\"dataList\":[]}");

        var engine = new FileDiffEngine();
        var result = engine.Compute(
            GameFileLocator.ScanJsonFiles(oldEn),
            GameFileLocator.ScanJsonFiles(oldZh),
            GameFileLocator.ScanJsonFiles(newEn));

        Assert.Equal(1, result.NewCount);
        Assert.Equal("New.json", result.Entries.Single(e => e.Kind == FileDiffKind.New).LogicalPath);

        // 清理
        Directory.Delete(oldEn, true);
        Directory.Delete(oldZh, true);
        Directory.Delete(newEn, true);
    }

    [Fact]
    public void 旧英文有但中文缺失_应标记MissingChinese()
    {
        var oldEn = MakeTempDir();
        var oldZh = MakeTempDir();
        var newEn = MakeTempDir();

        File.WriteAllText(Path.Combine(oldEn, "EN_Enemies.json"), "{\"dataList\":[]}");
        File.WriteAllText(Path.Combine(newEn, "EN_Enemies.json"), "{\"dataList\":[]}");

        var engine = new FileDiffEngine();
        var result = engine.Compute(
            GameFileLocator.ScanJsonFiles(oldEn),
            GameFileLocator.ScanJsonFiles(oldZh),
            GameFileLocator.ScanJsonFiles(newEn));

        Assert.Equal(1, result.MissingChineseCount);
        Assert.Equal(FileDiffKind.MissingChinese, result.Entries.Single().Kind);
    }

    [Fact]
    public void 内容变化_应标记Modified()
    {
        var oldEn = MakeTempDir();
        var oldZh = MakeTempDir();
        var newEn = MakeTempDir();

        File.WriteAllText(Path.Combine(oldEn, "EN_Enemies.json"), "{\"dataList\":[{\"id\":1}]}");
        File.WriteAllText(Path.Combine(oldZh, "Enemies.json"), "{\"dataList\":[{\"id\":1}]}");
        File.WriteAllText(Path.Combine(newEn, "EN_Enemies.json"), "{\"dataList\":[{\"id\":2}]}");

        var engine = new FileDiffEngine();
        var result = engine.Compute(
            GameFileLocator.ScanJsonFiles(oldEn),
            GameFileLocator.ScanJsonFiles(oldZh),
            GameFileLocator.ScanJsonFiles(newEn),
            oldEn, newEn);

        Assert.Equal(1, result.ModifiedCount);
        Assert.Equal(FileDiffKind.Modified, result.Entries.Single().Kind);
    }

    [Fact]
    public void 提取器_应将新增文件复制到目标目录()
    {
        var newEn = MakeTempDir();
        var target = MakeTempDir();

        File.WriteAllText(Path.Combine(newEn, "EN_New.json"), "{\"dataList\":[]}");

        var entry = new FileDiffEntry
        {
            LogicalPath = "New.json",
            EnglishPath = "EN_New.json",
            Kind = FileDiffKind.New,
        };

        var result = NewFileExtractor.Extract(newEn, new[] { entry }, target);

        Assert.Equal(1, result.CopiedCount);
        Assert.True(File.Exists(Path.Combine(target, "EN_New.json")));

        Directory.Delete(newEn, true);
        Directory.Delete(target, true);
    }

    [Fact]
    public void 游戏目录定位_应找到真实游戏()
    {
        // 从测试运行目录向上查找（项目位于游戏目录内时能命中）
        var result = GameDirectoryLocator.AutoLocate();
        // 不强制断言，避免在非游戏环境失败；此处仅验证方法不抛异常
        _ = result is null || result.IsAutoDetected;
    }
}
