using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第7轮（T-4）：配置 fail-closed 测试。
///
/// 核心断言：任何配置问题都必须让任务「拒绝启动」，
/// 绝不允许静默降级为 Mock（否则界面会显示翻译成功，实际什么都没翻译）。
/// </summary>
public class AppSettingsFailClosedTests : IDisposable
{
    private readonly string _configDir;

    public AppSettingsFailClosedTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "LT_CFG_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);

        // 默认写入合法提示词（除提示词专项用例外，都不应该因为提示词失败）
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

    private void WriteValidAppSettings(string apiKey = "sk-test-key")
        => WriteAppSettings($$"""
            {
              "deepSeek": {
                "apiUrl": "https://api.deepseek.com/chat/completions",
                "apiKey": "{{apiKey}}",
                "model": "deepseek-v4-flash",
                "temperature": 0.3,
                "maxTokens": 4096
              }
            }
            """);

    private SettingsLoadResult Load() => AppSettingsLoader.LoadProviderSettings(_configDir);

    [Fact]
    public void 配置文件不存在_必须失败且不回退Mock()
    {
        var result = Load();

        Assert.False(result.Success);
        Assert.Equal(TranslationProviderMode.DeepSeek, result.Mode);
        Assert.Contains(result.Errors, e => e.Contains("未找到配置文件"));
    }

    [Fact]
    public void JSON损坏_必须失败且错误信息可读()
    {
        WriteAppSettings("{ \"deepSeek\": { \"apiKey\": ");

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("不是合法 JSON"));
    }

    [Fact]
    public void 缺少deepSeek段_必须失败()
    {
        WriteAppSettings("""{ "provider": "deepseek", "batch": { "maxItemsPerBatch": 20 } }""");

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("缺少 deepSeek 配置段"));
    }

    [Fact]
    public void 缺少ApiKey_必须失败()
    {
        WriteAppSettings("""
            { "deepSeek": { "apiUrl": "https://api.deepseek.com/chat/completions", "model": "m" } }
            """);

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("apiKey"));
    }

    [Fact]
    public void ApiKey为空串_必须失败()
    {
        WriteValidAppSettings(apiKey: "   ");

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("apiKey 为空"));
    }

    [Fact]
    public void 缺少Model_必须失败()
    {
        WriteAppSettings("""
            { "deepSeek": { "apiUrl": "https://api.deepseek.com/chat/completions", "apiKey": "sk-x" } }
            """);

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("model"));
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://api.deepseek.com/chat")]
    [InlineData("/chat/completions")]
    public void BaseUrl非法_必须失败(string badUrl)
    {
        WriteAppSettings($$"""
            { "deepSeek": { "apiUrl": "{{badUrl}}", "apiKey": "sk-x", "model": "m" } }
            """);

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("apiUrl"));
    }

    [Fact]
    public void Temperature类型错误_必须失败()
    {
        WriteAppSettings("""
            { "deepSeek": { "apiUrl": "https://api.deepseek.com/chat/completions", "apiKey": "sk-x",
                            "model": "m", "temperature": "热" } }
            """);

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("temperature"));
    }

    [Fact]
    public void MaxTokens超出范围_必须失败()
    {
        WriteAppSettings("""
            { "deepSeek": { "apiUrl": "https://api.deepseek.com/chat/completions", "apiKey": "sk-x",
                            "model": "m", "maxTokens": 0 } }
            """);

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("maxTokens"));
    }

    [Fact]
    public void Provider取值非法_必须失败()
    {
        WriteAppSettings("""{ "provider": "openai", "deepSeek": { "apiKey": "sk-x" } }""");

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("provider 取值无效"));
    }

    [Fact]
    public void 提示词配置缺失_真实模式必须失败()
    {
        WriteValidAppSettings();
        File.Delete(Path.Combine(_configDir, "prompt.json"));

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("提示词配置"));
    }

    [Fact]
    public void 提示词配置损坏_真实模式必须失败()
    {
        WriteValidAppSettings();
        File.WriteAllText(Path.Combine(_configDir, "prompt.json"), "{ 坏的 json");

        var result = Load();

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("提示词配置"));
    }

    [Fact]
    public void 合法DeepSeek配置_成功且字段正确()
    {
        WriteValidAppSettings();

        var result = Load();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal(TranslationProviderMode.DeepSeek, result.Mode);
        Assert.Equal("sk-test-key", result.Options.ApiKey);
        Assert.Equal("https://api.deepseek.com/chat/completions", result.Options.ApiUrl);
        Assert.Equal("deepseek-v4-flash", result.Options.Model);
        Assert.Equal(0.3, result.Options.Temperature, 3);
        Assert.Equal(4096, result.Options.MaxTokens);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void 大写键名配置_同样可以成功读取()
    {
        // 真实 config/appsettings.json 使用 ApiUrl / ApiKey / Model 等大写键名
        WriteAppSettings("""
            {
              "deepSeek": {
                "ApiUrl": "https://api.deepseek.com/chat/completions",
                "apiKey": "sk-upper",
                "Model": "deepseek-v4-flash"
              }
            }
            """);

        var result = Load();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal("sk-upper", result.Options.ApiKey);
        Assert.Equal("https://api.deepseek.com/chat/completions", result.Options.ApiUrl);
    }

    [Fact]
    public void 显式选择Mock_成功且无需ApiKey()
    {
        WriteAppSettings("""{ "provider": "mock" }""");

        var result = Load();

        Assert.True(result.Success, string.Join("；", result.Errors));
        Assert.Equal(TranslationProviderMode.Mock, result.Mode);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void 显式选择Mock_存在ApiKey时给出警告()
    {
        WriteAppSettings("""{ "provider": "mock", "deepSeek": { "apiKey": "sk-should-not-be-used" } }""");

        var result = Load();

        Assert.True(result.Success);
        Assert.Equal(TranslationProviderMode.Mock, result.Mode);
        Assert.Contains(result.Warnings, w => w.Contains("API Key 不会被使用"));
    }

    [Fact]
    public void 保存配置_必须保留已有的provider字段()
    {
        WriteAppSettings("""{ "provider": "mock", "deepSeek": { "apiKey": "" } }""");

        AppSettingsLoader.SaveDeepSeek(_configDir, new DeepSeekOptions { ApiKey = "sk-new" });

        using var doc = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_configDir, "appsettings.json")));
        Assert.Equal("mock", doc.RootElement.GetProperty("provider").GetString());   // 未被抹掉
        Assert.Equal(
            "sk-new",
            doc.RootElement.GetProperty("deepSeek").GetProperty("ApiKey").GetString());
        Assert.Equal(TranslationProviderMode.Mock, Load().Mode);
    }

    [Fact]
    public void 配置无效_启动助手拒绝创建任何Provider()
    {
        WriteAppSettings("{ 坏的 json");
        var logs = new List<string>();

        var bootstrap = TranslationRunBootstrap.CreateProvider(
            Load(), _configDir, cacheServices: null, log: logs.Add);

        Assert.False(bootstrap.CanStart);
        Assert.Null(bootstrap.Provider);                                   // 关键：没有 Mock 兜底
        Assert.Contains(logs, l => l.Contains("[错误]"));
        Assert.Contains(logs, l => l.Contains("不会自动改用模拟翻译"));
        Assert.DoesNotContain(logs, l => l.Contains("Mock（测试模式）"));
    }

    [Fact]
    public void 显式Mock_启动助手创建Mock并输出明显日志()
    {
        WriteAppSettings("""{ "provider": "mock" }""");
        var logs = new List<string>();

        var bootstrap = TranslationRunBootstrap.CreateProvider(
            Load(), _configDir, cacheServices: null, log: logs.Add);

        Assert.True(bootstrap.CanStart);
        Assert.True(bootstrap.IsMock);
        Assert.IsType<MockTranslationProvider>(bootstrap.Provider);
        Assert.Contains(logs, l => l.Contains("当前翻译Provider：Mock（测试模式）"));
    }

    [Fact]
    public void 合法DeepSeek_启动助手创建真实Provider且可释放()
    {
        WriteValidAppSettings();
        var logs = new List<string>();

        var bootstrap = TranslationRunBootstrap.CreateProvider(
            Load(), _configDir, cacheServices: null, log: logs.Add);

        Assert.True(bootstrap.CanStart);
        Assert.False(bootstrap.IsMock);
        Assert.IsType<DeepSeekTranslationProvider>(bootstrap.Provider);
        Assert.Contains(logs, l => l.Contains("当前翻译Provider：DeepSeek"));

        var disposable = Assert.IsAssignableFrom<IDisposable>(bootstrap.Provider);
        disposable.Dispose();
        disposable.Dispose();   // 幂等
    }
}
