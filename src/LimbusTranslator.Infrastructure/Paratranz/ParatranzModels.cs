namespace LimbusTranslator.Infrastructure.Paratranz;

/// <summary>Paratranz 远程术语同步配置（第8.88轮；最小配置，不硬编码任何凭据）。</summary>
public sealed class ParatranzOptions
{
    /// <summary>是否启用远程术语（默认关闭，用户显式开启才参与合并）</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Paratranz 项目 ID（默认 6860 = KIDJourney/limbus_translate 使用的项目）。
    /// 注意：第8.89轮实测该 API 在当前环境返回 **HTTP 403**，因此**真实接口未验证**，仅作为默认值。
    /// </summary>
    public string? ProjectId { get; set; } = "6860";

    /// <summary>缓存文件路径（相对项目根或绝对路径）</summary>
    public string CachePath { get; set; } = Path.Combine("data", "cache", "paratranz_glossary.json");

    /// <summary>请求超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>API 基址（可用测试替身覆盖）</summary>
    public string BaseUrl { get; set; } = "https://paratranz.cn/api";

    /// <summary>每页条数（分页读取）</summary>
    public int PageSize { get; set; } = 500;

    /// <summary>最大页数保护（避免异常接口导致无限循环）</summary>
    public int MaxPages { get; set; } = 20;
}

/// <summary>远程术语条目（缓存内保存的最小字段集）。</summary>
public sealed class ParatranzTermEntry
{
    /// <summary>术语原文</summary>
    public required string Term { get; init; }

    /// <summary>术语译名</summary>
    public required string Translation { get; init; }

    /// <summary>远程条目 ID（服务端提供时才有）</summary>
    public int? RemoteId { get; init; }

    /// <summary>远程更新时间（服务端提供时才有）</summary>
    public string? UpdatedAt { get; init; }
}

/// <summary>远程术语缓存文件模型。</summary>
public sealed class ParatranzGlossaryCache
{
    /// <summary>缓存结构版本</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>项目 ID</summary>
    public string? ProjectId { get; init; }

    /// <summary>抓取时间（UTC）</summary>
    public DateTime FetchedAtUtc { get; init; }

    /// <summary>来源标识（如 paratranz:/projects/{id}/terms）</summary>
    public string? Source { get; init; }

    /// <summary>规范化后的有效条目</summary>
    public IReadOnlyList<ParatranzTermEntry> Entries { get; init; } = Array.Empty<ParatranzTermEntry>();

    /// <summary>规范化阶段的审计统计（诊断用）</summary>
    public ParatranzAuditSummary Audit { get; init; } = new();

    /// <summary>当前缓存结构版本</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>远程数据规范化审计结果（不因远程存在就假定其正确）。</summary>
public sealed class ParatranzAuditSummary
{
    /// <summary>抓取到的原始条目数（含分页）</summary>
    public int RawCount { get; init; }

    /// <summary>空 Source / Source 纯空白而丢弃</summary>
    public int DroppedEmptySource { get; init; }

    /// <summary>空 Target / Target 纯空白而丢弃</summary>
    public int DroppedEmptyTarget { get; init; }

    /// <summary>完全重复而丢弃</summary>
    public int DroppedDuplicate { get; init; }

    /// <summary>同 Source 多 Target 冲突组数</summary>
    public int MultiTargetConflicts { get; init; }

    /// <summary>Target 中仍含韩文而丢弃</summary>
    public int DroppedHangulTarget { get; init; }

    /// <summary>Source == Target（明显未翻译）而丢弃</summary>
    public int DroppedSameAsSource { get; init; }

    /// <summary>重复 RemoteId 组数</summary>
    public int DuplicateRemoteIds { get; init; }

    /// <summary>有效条目数</summary>
    public int EffectiveCount { get; init; }
}