namespace LimbusTranslator.Infrastructure.Configuration;

/// <summary>
/// 用户请求的翻译 Provider 类型（来自 config/appsettings.json 的 provider 字段）。
///
/// 默认值必须是 <see cref="DeepSeek"/>：只有用户显式写 <c>"provider": "mock"</c> 时才允许使用模拟翻译。
/// </summary>
public enum TranslationProviderMode
{
    /// <summary>真实模型 Provider（默认）。配置不完整时必须 fail-closed，禁止回退 Mock。</summary>
    DeepSeek = 0,

    /// <summary>模拟 Provider（仅测试 / 开发；必须由用户显式选择）。</summary>
    Mock = 1,
}

/// <summary>
/// 配置读取结果（第7轮）。
///
/// 关键约定（fail-closed）：
///   Success = false 时，调用方必须终止任务并展示 <see cref="Errors"/>，
///   禁止自动切换到 Mock —— 防止出现「配置损坏 → 静默 Mock → 界面显示翻译成功」的假成功。
/// </summary>
public sealed class SettingsLoadResult
{
    /// <summary>配置是否有效（true 才允许启动翻译）。</summary>
    public required bool Success { get; init; }

    /// <summary>解析出的 DeepSeek 配置（Mock 模式下为默认值）。</summary>
    public required DeepSeekOptions Options { get; init; }

    /// <summary>
    /// Provider 请求分批配置（config 的 batch 段，第7.5轮）。
    /// 缺失字段使用默认值并记录 Warning；字段存在但非法则视为配置错误。
    /// </summary>
    public BatchOptions Batch { get; init; } = new();

    /// <summary>用户请求的 Provider 类型。</summary>
    public required TranslationProviderMode Mode { get; init; }

    /// <summary>配置错误（Success = false 时至少一条）。</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>配置警告（不影响启动）。</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>实际读取的配置文件路径（便于用户定位）。</summary>
    public string? ConfigPath { get; init; }

    /// <summary>错误摘要（单行，供 UI 直接展示）。</summary>
    public string ErrorSummary => Errors.Count == 0 ? string.Empty : string.Join("；", Errors);
}
