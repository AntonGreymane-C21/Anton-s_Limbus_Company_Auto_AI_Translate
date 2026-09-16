using System.Net;
using System.Text.Json;
using LimbusTranslator.Infrastructure.Security;

namespace LimbusTranslator.Infrastructure.Paratranz;

/// <summary>同步状态（错误必须可区分）。</summary>
public enum ParatranzSyncStatus
{
    /// <summary>成功</summary>
    Success,

    /// <summary>未启用 / 配置缺失（含 401/403/404）</summary>
    ConfigError,

    /// <summary>网络失败</summary>
    NetworkError,

    /// <summary>超时</summary>
    Timeout,

    /// <summary>HTTP 错误（429 / 5xx / 其它）</summary>
    HttpError,

    /// <summary>接口格式变化 / JSON 解析失败 / 缓存写入失败</summary>
    FormatError,

    /// <summary>无有效条目（不覆盖旧缓存）</summary>
    NoEntries,
}

/// <summary>同步结果。</summary>
public sealed class ParatranzSyncResult
{
    /// <summary>是否成功</summary>
    public required bool Success { get; init; }

    /// <summary>状态</summary>
    public required ParatranzSyncStatus Status { get; init; }

    /// <summary>用户可读消息（已脱敏）</summary>
    public required string Message { get; init; }

    /// <summary>远程原始条目数（规范化前）</summary>
    public int RawCount { get; init; }

    /// <summary>有效条目数</summary>
    public int EffectiveCount { get; init; }

    /// <summary>审计统计</summary>
    public ParatranzAuditSummary Audit { get; init; } = new();

    /// <summary>失败时是否保留了旧缓存</summary>
    public bool KeptPreviousCache { get; init; }

    /// <summary>缓存路径</summary>
    public string? CachePath { get; init; }

    /// <summary>耗时（毫秒）</summary>
    public long DurationMs { get; init; }
}
