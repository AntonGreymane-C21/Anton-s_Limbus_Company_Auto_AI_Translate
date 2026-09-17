using LimbusTranslator.Infrastructure.Configuration;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.16轮：**maxTokens 必须按服务端真实允许范围校验**。
///
/// 真实故障：配置写成 1000000 —— 旧校验上限是 1,000,000（写错），于是配置"合法"，
/// 请求却被 DeepSeek 以 400 invalid_request_error 拒绝。
/// 现在上限 = 393216（服务端真实上限），超范围 ⇒ 报错 + 保持安全默认值 8192。
/// </summary>
public sealed class AppSettingsMaxTokensTests : IDisposable
{
    private readonly string _configDir;

    public AppSettingsMaxTokensTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "LT_MaxTok_" + Guid.NewGuid().ToString("N"));
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
            // 清理失败可忽略
        }
    }

    private SettingsLoadResult LoadWithMaxTokens(string? maxTokensJson)
    {
        var maxTokensLine = maxTokensJson is null ? string.Empty : $",\n  \"maxTokens\": {maxTokensJson}";
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), $$"""
            {
              "deepSeek": {
                "apiUrl": "https://api.deepseek.com/chat/completions",
                "apiKey": "sk-test-key",
                "model": "deepseek-v4-flash",
                "temperature": 0.3{{maxTokensLine}}
              }
            }
            """);
        return AppSettingsLoader.LoadProviderSettings(_configDir);
    }

    [Fact]
    public void 超出服务端上限的maxTokens必须被拒绝并给出建议()
    {
        var result = LoadWithMaxTokens("1000000");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("maxTokens 超出允许范围"));
        Assert.Contains(result.Errors, e => e.Contains("393216"));
        Assert.Contains(result.Errors, e => e.Contains("建议 8192"));
        Assert.Equal(8192, result.Options.MaxTokens);   // 回落到安全默认值，绝不把非法值发出去
    }

    [Fact]
    public void 合法maxTokens必须原样生效()
    {
        var result = LoadWithMaxTokens("16384");

        Assert.True(result.Success);
        Assert.Equal(16384, result.Options.MaxTokens);
    }

    [Fact]
    public void 缺省maxTokens使用安全默认值()
    {
        var result = LoadWithMaxTokens(null);

        Assert.True(result.Success);
        Assert.Equal(8192, result.Options.MaxTokens);
    }
}
