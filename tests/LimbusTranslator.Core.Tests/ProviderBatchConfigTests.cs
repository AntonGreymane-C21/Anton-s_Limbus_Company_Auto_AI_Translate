using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第7.5轮：batch 配置读取与 fail-closed 校验测试。
///
/// 规则：
///   字段缺失 → 默认值 + Warning（明确写出「未配置 xxx，使用默认值 N」）
///   字段存在但非法（类型错 / 非整数 / 超范围）→ Error，任务拒绝启动
/// </summary>
public class ProviderBatchConfigTests : IDisposable
{
    private readonly string _configDir;

    public ProviderBatchConfigTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "LT_BATCH_CFG_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(
            Path.Combine(_configDir, "prompt.json"),
            """{"systemPrompt":"测试用提示词","outputFormat":"{\"items\":[]}"}""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, true);
        }
        catch
        {
            // 临时目录清理失败可忽略
        }
    }

    private void WriteAppSettings(string json)
        => File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), json);

    private void WriteWithBatch(string batchJson)
        => WriteAppSettings($$"""
            {
              "deepSeek": {
                "apiUrl": "https://api.deepseek.com/chat/completions",
                "apiKey": "sk-batch-test",
                "model": "deepseek-v4-flash"
              },
              "batch": {{batchJson}}
            }
            """);

    private SettingsLoadResult Load() => AppSettingsLoader.LoadProviderSettings(_configDir);

    [Fact]
    public void 未配置batch段_使用默认值并给出警告()
    {
        WriteAppSettings("""
            { "deepSeek": { "apiUrl": "https://api.deepseek.com/chat/completions",
                            "apiKey": "sk-x", "model": "m" } }
            """);

        var result = Load();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal(20, result.Batch.MaxItemsPerBatch);
        Assert.Equal(30000, result.Batch.MaxCharactersPerBatch);
        Assert.Contains(result.Warnings, w => w.Contains("未配置 batch.maxItemsPerBatch，使用默认值 20"));
        Assert.Contains(result.Warnings, w => w.Contains("未配置 batch.maxCharactersPerBatch，使用默认值 30000"));
    }

    [Fact]
    public void MaxItems为10_实际读取10()
    {
        WriteWithBatch("""{ "maxItemsPerBatch": 10 }""");

        var result = Load();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal(10, result.Batch.MaxItemsPerBatch);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("maxItemsPerBatch"));   // 已配置 → 不告警
        Assert.Contains(result.Warnings, w => w.Contains("maxCharactersPerBatch"));     // 未配置 → 告警
    }

    [Fact]
    public void MaxCharacters为12000_实际读取12000()
    {
        WriteWithBatch("""{ "maxItemsPerBatch": 5, "maxCharactersPerBatch": 12000, "targetInputTokens": 8000 }""");

        var result = Load();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal(5, result.Batch.MaxItemsPerBatch);
        Assert.Equal(12000, result.Batch.MaxCharactersPerBatch);
        Assert.Equal(8000, result.Batch.TargetInputTokens);
        Assert.Empty(result.Warnings);
    }

    [Theory]
    [InlineData("""{ "maxItemsPerBatch": 0 }""")]
    [InlineData("""{ "maxItemsPerBatch": -3 }""")]
    [InlineData("""{ "maxItemsPerBatch": 999999999 }""")]
    [InlineData("""{ "maxItemsPerBatch": 20.5 }""")]
    [InlineData("""{ "maxItemsPerBatch": "20" }""")]
    public void MaxItems非法_必须失败(string batchJson)
    {
        WriteWithBatch(batchJson);

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("batch.maxItemsPerBatch"));
    }

    [Theory]
    [InlineData("""{ "maxCharactersPerBatch": 0 }""")]
    [InlineData("""{ "maxCharactersPerBatch": 2147483647 }""")]
    [InlineData("""{ "maxCharactersPerBatch": 3000.5 }""")]
    [InlineData("""{ "maxCharactersPerBatch": true }""")]
    public void MaxCharacters非法_必须失败(string batchJson)
    {
        WriteWithBatch(batchJson);

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("batch.maxCharactersPerBatch"));
    }

    [Fact]
    public void TargetInputTokens非法_必须失败()
    {
        WriteWithBatch("""{ "targetInputTokens": 0 }""");

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("batch.targetInputTokens"));
    }

    [Fact]
    public void batch段不是对象_必须失败()
    {
        WriteWithBatch("20");

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("batch 段必须是 JSON 对象"));
    }

    [Fact]
    public void 配置无效时_启动助手拒绝启动()
    {
        WriteWithBatch("""{ "maxItemsPerBatch": 0 }""");
        var logs = new List<string>();

        var bootstrap = TranslationRunBootstrap.CreateProvider(Load(), _configDir, null, logs.Add);

        Assert.False(bootstrap.CanStart);
        Assert.Null(bootstrap.Provider);
        Assert.Contains(logs, l => l.Contains("[错误]"));
    }

    [Fact]
    public void 配置有效时_启动助手输出分批信息()
    {
        WriteWithBatch("""{ "maxItemsPerBatch": 7, "maxCharactersPerBatch": 9000 }""");
        var logs = new List<string>();

        var bootstrap = TranslationRunBootstrap.CreateProvider(Load(), _configDir, null, logs.Add);

        Assert.True(bootstrap.CanStart);
        Assert.Contains(logs, l => l.Contains("Provider 请求分批") && l.Contains("maxItemsPerBatch=7")
                                   && l.Contains("maxCharactersPerBatch=9000"));
        (bootstrap.Provider as IDisposable)?.Dispose();
    }

    [Fact]
    public void Mock模式不校验batch配置()
    {
        // 模拟模式不产生 API 请求，因此 batch 配置非法不影响启动
        WriteAppSettings("""{ "provider": "mock", "batch": { "maxItemsPerBatch": 0 } }""");

        var result = Load();

        Assert.True(result.Success);
        Assert.Equal(TranslationProviderMode.Mock, result.Mode);
        Assert.Equal(20, result.Batch.MaxItemsPerBatch);      // 使用默认值
    }
}
