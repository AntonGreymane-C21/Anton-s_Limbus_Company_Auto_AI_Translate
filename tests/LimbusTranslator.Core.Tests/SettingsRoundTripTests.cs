using LimbusTranslator.Infrastructure.Configuration;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.8轮：GUI 设置读写往返测试（设置窗口保存后再次读取必须一致）。
///
/// 重点：保存 API 设置时**不得丢失** batch / provider 段（第8.75轮以前会丢）。
/// </summary>
public class SettingsRoundTripTests : IDisposable
{
    private readonly string _configDir;

    public SettingsRoundTripTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "LT_SETTINGS_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_configDir);
        File.WriteAllText(
            Path.Combine(_configDir, "prompt.json"),
            """{"systemPrompt":"测试提示词","outputFormat":"{}"}""");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_configDir, true);
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private SettingsLoadResult Load() => AppSettingsLoader.LoadProviderSettings(_configDir);

    private static DeepSeekOptions Options(TranslationThinkingMode mode, string? effort = null) => new()
    {
        ApiKey = "sk-gui-test",
        ApiUrl = "https://api.deepseek.com/chat/completions",
        Model = "deepseek-flash",
        ThinkingMode = mode,
        ReasoningEffort = effort,
    };

    [Theory]
    [InlineData(TranslationThinkingMode.Adaptive, "自适应（推荐）")]
    [InlineData(TranslationThinkingMode.AlwaysOn, "始终开启")]
    [InlineData(TranslationThinkingMode.AlwaysOff, "始终关闭")]
    public void Thinking模式保存后重新读取一致(TranslationThinkingMode mode, string _)
    {
        AppSettingsLoader.SaveDeepSeek(_configDir, Options(mode), BatchOptions.Default);

        var reloaded = Load();

        Assert.True(reloaded.Success, string.Join("；", reloaded.Errors));
        Assert.Equal(mode, reloaded.Options.ThinkingMode);
    }

    [Fact]
    public void 思考强度保存后重新读取一致()
    {
        AppSettingsLoader.SaveDeepSeek(_configDir, Options(TranslationThinkingMode.Adaptive, "low"), BatchOptions.Default);

        var reloaded = Load();

        Assert.True(reloaded.Success, string.Join("；", reloaded.Errors));
        Assert.Equal("low", reloaded.Options.ReasoningEffort);
    }

    [Fact]
    public void 思考强度留空_保存为空且不报错()
    {
        AppSettingsLoader.SaveDeepSeek(_configDir, Options(TranslationThinkingMode.Adaptive), BatchOptions.Default);

        var reloaded = Load();

        Assert.True(reloaded.Success, string.Join("；", reloaded.Errors));
        Assert.True(string.IsNullOrEmpty(reloaded.Options.ReasoningEffort));
    }

    [Fact]
    public void batch配置保存后重新读取一致()
    {
        var batch = new BatchOptions { MaxItemsPerBatch = 7, MaxCharactersPerBatch = 9000, TargetInputTokens = 8000 };

        AppSettingsLoader.SaveDeepSeek(_configDir, Options(TranslationThinkingMode.Adaptive), batch);

        var reloaded = Load();

        Assert.Equal(7, reloaded.Batch.MaxItemsPerBatch);
        Assert.Equal(9000, reloaded.Batch.MaxCharactersPerBatch);
        Assert.Equal(8000, reloaded.Batch.TargetInputTokens);
    }

    [Fact]
    public void 未传batch时_保存必须保留文件中已有batch段()
    {
        // 先写入一份带 batch 的配置（模拟用户手工/旧版写入）
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), """
            {
              "provider": "deepseek",
              "deepSeek": { "apiUrl": "https://api.deepseek.com/chat/completions", "apiKey": "sk-x", "model": "m" },
              "batch": { "maxItemsPerBatch": 11, "maxCharactersPerBatch": 12000, "targetInputTokens": 5000 }
            }
            """);

        AppSettingsLoader.SaveDeepSeek(_configDir, Options(TranslationThinkingMode.AlwaysOn));

        var reloaded = Load();
        Assert.Equal(11, reloaded.Batch.MaxItemsPerBatch);
        Assert.Equal(12000, reloaded.Batch.MaxCharactersPerBatch);
        Assert.Equal(5000, reloaded.Batch.TargetInputTokens);
        Assert.Equal(TranslationThinkingMode.AlwaysOn, reloaded.Options.ThinkingMode);
    }

    [Fact]
    public void 旧配置只有thinking布尔值_映射为always_on或always_off()
    {
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), """
            {
              "deepSeek": { "apiUrl": "https://api.deepseek.com/chat/completions", "apiKey": "sk-x", "model": "m",
                            "thinking": true }
            }
            """);

        Assert.Equal(TranslationThinkingMode.AlwaysOn, Load().Options.ThinkingMode);

        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), """
            {
              "deepSeek": { "apiUrl": "https://api.deepseek.com/chat/completions", "apiKey": "sk-x", "model": "m",
                            "thinking": false }
            }
            """);

        Assert.Equal(TranslationThinkingMode.AlwaysOff, Load().Options.ThinkingMode);
    }

    [Fact]
    public void provider字段在保存后保持不变()
    {
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), """
            { "provider": "mock", "deepSeek": { "apiKey": "" } }
            """);

        AppSettingsLoader.SaveDeepSeek(_configDir, Options(TranslationThinkingMode.Adaptive), BatchOptions.Default);

        Assert.Equal(TranslationProviderMode.Mock, Load().Mode);
    }
}
