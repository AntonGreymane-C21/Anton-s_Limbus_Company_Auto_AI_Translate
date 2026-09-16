namespace LimbusTranslator.Infrastructure.Configuration;

/// <summary>
/// DeepSeek 配置。
/// 从 config/appsettings.json 读取；仓库中只保留 appsettings.example.json。
/// </summary>
public sealed class DeepSeekOptions
{
    /// <summary>API 地址</summary>
    public string ApiUrl { get; set; } = "https://api.deepseek.com/chat/completions";

    /// <summary>API Key（禁止硬编码）</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>模型名</summary>
    public string Model { get; set; } = "deepseek-v4-flash";

    /// <summary>是否开启思考模式</summary>
    public bool Thinking { get; set; }

    /// <summary>
    /// 思考强度（对应 DeepSeek `reasoning_effort`：low / high / max）。
    /// 仅在思考开启时随请求发送；为空表示不发送（使用服务端默认 high）。
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Thinking 模式（第8.75轮）：adaptive / always_on / always_off。
    ///
    /// <c>null</c> = **未显式设置**，由 <c>TranslationThinkingPolicy.FromOptions</c> 解析：
    ///   ThinkingMode 已设置 → 使用它；
    ///   否则 Thinking == true → AlwaysOn（旧 API 语义）；
    ///   否则 → Adaptive（生产默认）。
    /// 注意：配置文件里只要出现 <c>thinking</c> 或 <c>thinkingMode</c>，AppSettingsLoader 都会显式写入本字段。
    /// </summary>
    public TranslationThinkingMode? ThinkingMode { get; set; }

    /// <summary>采样温度</summary>
    public double Temperature { get; set; } = 0.3;

    /// <summary>最大输出 Token</summary>
    public int MaxTokens { get; set; } = 4096;

    /// <summary>请求超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>最大重试次数</summary>
    public int MaxRetry { get; set; } = 5;

    /// <summary>最大并发请求数</summary>
    public int MaxConcurrentRequests { get; set; } = 10;
}
