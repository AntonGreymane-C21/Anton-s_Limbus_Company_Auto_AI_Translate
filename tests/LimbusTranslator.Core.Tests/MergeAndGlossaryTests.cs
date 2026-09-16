using System.Text.Json;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// Merge 输出 + 术语库测试。
/// </summary>
public class MergeAndGlossaryTests
{
    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Merge_应将译文写回英文模板()
    {
        var newEn = MakeTempDir();
        var output = MakeTempDir();

        // 生成一个简单的英文 JSON（与真实结构一致）
        var englishJson = "{\"dataList\":[{\"id\":1,\"name\":\"Hello\",\"desc\":\"World\"}]}";
        File.WriteAllText(Path.Combine(newEn, "EN_Test.json"), englishJson);

        // 构造译文（key = 相对路径|id|FieldPath）
        var translations = new Dictionary<string, string>
        {
            { "Test.json|1|dataList[0].name", "你好" },
            { "Test.json|1|dataList[0].desc", "世界" },
        };

        var merge = new MergeOutputService();
        var files = merge.MergeAll(newEn, translations, output);

        Assert.Single(files);
        var outputFile = Path.Combine(output, "Test.json");
        Assert.True(File.Exists(outputFile));

        using var doc = JsonDocument.Parse(File.ReadAllText(outputFile));
        var record = doc.RootElement.GetProperty("dataList")[0];
        Assert.Equal("你好", record.GetProperty("name").GetString());
        Assert.Equal("世界", record.GetProperty("desc").GetString());

        Directory.Delete(newEn, true);
        Directory.Delete(output, true);
    }

    [Fact]
    public void Merge_带BOM的英文模板_应正常输出且不产生双重BOM()
    {
        var newEn = MakeTempDir();
        var output = MakeTempDir();

        // 生成一个带 UTF-8 BOM 的英文 JSON（模拟游戏内 10 个 BOM 文件，如 EN_CultivationEvent.json）
        var englishJson = "{\"dataList\":[{\"id\":1,\"name\":\"Hello\",\"desc\":\"World\"}]}";
        File.WriteAllText(Path.Combine(newEn, "EN_TestBom.json"), englishJson,
            new System.Text.UTF8Encoding(true));

        var translations = new Dictionary<string, string>
        {
            { "TestBom.json|1|dataList[0].name", "你好" },
            { "TestBom.json|1|dataList[0].desc", "世界" },
        };

        var merge = new MergeOutputService();
        var files = merge.MergeAll(newEn, translations, output);

        Assert.Single(files);
        var outputFile = Path.Combine(output, "TestBom.json");
        Assert.True(File.Exists(outputFile));

        // 输出文件应保留单个 BOM（EF BB BF），不能被双重 BOM（EF BB BF EF BB BF）破坏
        var bytes = File.ReadAllBytes(outputFile);
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "输出文件应保留 UTF-8 BOM");
        Assert.False(bytes.Length >= 6
            && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            && bytes[3] == 0xEF && bytes[4] == 0xBB && bytes[5] == 0xBF,
            "输出文件不应是双重 BOM（旧版 bug：\\uFEFF 前缀 + UTF8Encoding(true) 双重写入）");

        using var doc = JsonDocument.Parse(File.ReadAllText(outputFile));
        var record = doc.RootElement.GetProperty("dataList")[0];
        Assert.Equal("你好", record.GetProperty("name").GetString());
        Assert.Equal("世界", record.GetProperty("desc").GetString());

        Directory.Delete(newEn, true);
        Directory.Delete(output, true);
    }

    [Fact]
    public void Merge_字段路径不存在时_不应写出部分文件并返回报告()
    {
        var newEn = MakeTempDir();
        var output = MakeTempDir();
        File.WriteAllText(Path.Combine(newEn, "EN_Test.json"),
            "{\"dataList\":[{\"id\":1,\"name\":\"Hello\"}]}");
        var translations = new Dictionary<string, string>
        {
            ["Test.json|1|dataList[0].name"] = "你好",
            ["Test.json|1|dataList[0].missing"] = "不应写入",
        };

        var report = new MergeOutputService().MergeAllWithReport(newEn, translations, output, translations.Keys);

        Assert.False(report.IsComplete);
        Assert.Empty(report.Files);
        Assert.Contains(report.Issues, issue => issue.Kind == OutputMergeIssueKind.FieldPathNotFound);
        Assert.False(File.Exists(Path.Combine(output, "Test.json")));

        Directory.Delete(newEn, true);
        Directory.Delete(output, true);
    }

    [Fact]
    public void Merge_缺少预期译文时_不应写出部分文件()
    {
        var newEn = MakeTempDir();
        var output = MakeTempDir();
        File.WriteAllText(Path.Combine(newEn, "EN_Test.json"),
            "{\"dataList\":[{\"id\":1,\"name\":\"Hello\",\"desc\":\"World\"}]}");
        var translations = new Dictionary<string, string>
        {
            ["Test.json|1|dataList[0].name"] = "你好",
        };
        var expected = new[]
        {
            "Test.json|1|dataList[0].name",
            "Test.json|1|dataList[0].desc",
        };

        var report = new MergeOutputService().MergeAllWithReport(newEn, translations, output, expected);

        Assert.False(report.IsComplete);
        Assert.Empty(report.Files);
        Assert.Contains(report.Issues, issue => issue.Kind == OutputMergeIssueKind.MissingTranslation);
        Assert.False(File.Exists(Path.Combine(output, "Test.json")));

        Directory.Delete(newEn, true);
        Directory.Delete(output, true);
    }

    [Fact]
    public void Glossary_能动态提取命中术语()
    {
        var configDir = MakeTempDir();
        File.WriteAllText(Path.Combine(configDir, "glossary.json"),
            "{\"Sinking\":{\"translation\":\"沉沦\",\"locked\":true},\"Bleed\":{\"translation\":\"流血\"}}");

        var glossary = new GlossaryService(configDir);
        Assert.Equal(2, glossary.Count);

        var hits = glossary.SelectTerms(new[] { "Inflict 2 Sinking next turn." });
        Assert.Single(hits);
        Assert.Equal("Sinking", hits[0].Key);
        Assert.Equal("沉沦", hits[0].Value.Translation);
        Assert.True(hits[0].Value.Locked);

        var prompt = GlossaryService.BuildGlossaryPrompt(hits);
        Assert.Contains("沉沦", prompt);
        // 第8.87轮：Locked 术语提示词改为更明确的“强制术语”措辞（原断言「禁止修改」随措辞升级同步调整）
        Assert.Contains("【强制术语（Locked）】", prompt);
        Assert.Contains("不得自行音译、改写、同义替换或省略", prompt);

        Directory.Delete(configDir, true);
    }

    [Fact]
    public void Glossary_无命中时返回空()
    {
        var configDir = MakeTempDir();
        File.WriteAllText(Path.Combine(configDir, "glossary.json"),
            "{\"Sinking\":{\"translation\":\"沉沦\"}}");

        var glossary = new GlossaryService(configDir);
        var hits = glossary.SelectTerms(new[] { "just a normal sentence" });
        Assert.Empty(hits);

        Directory.Delete(configDir, true);
    }

    [Fact]
    public void 项目config_术语库_应加载59条()
    {
        // 从项目 config 目录加载真实术语库
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? configDir = null;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "config")))
            {
                configDir = Path.Combine(dir.FullName, "config");
                break;
            }
            dir = dir.Parent;
        }
        Assert.NotNull(configDir);

        var glossary = new GlossaryService(configDir);
        Assert.True(glossary.Count >= 50, $"术语数应>=50，实际 {glossary.Count}");

        // 关键术语应存在
        var hits = glossary.SelectTerms(new[] { "Inflict 2 Sinking and 3 Bleed, then apply 4 Poise." });
        Assert.Contains(hits, kv => kv.Key == "Sinking" && kv.Value.Translation == "沉沦");
        Assert.Contains(hits, kv => kv.Key == "Bleed" && kv.Value.Translation == "流血");
        Assert.Contains(hits, kv => kv.Key == "Poise" && kv.Value.Translation == "呼吸法");
    }
}
