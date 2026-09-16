namespace LimbusTranslator.Infrastructure.Persistence;

/// <summary>
/// TranslationMemory 配置。
/// </summary>
public sealed class TranslationMemoryOptions
{
    /// <summary>SQLite 数据库文件路径</summary>
    public required string DatabasePath { get; init; }

    /// <summary>SQLite busy_timeout 毫秒数（默认 5000）</summary>
    public int BusyTimeoutMs { get; init; } = 5000;

    /// <summary>是否启用 WAL 模式（默认 true）</summary>
    public bool EnableWal { get; init; } = true;
}
