using System.Text.Json;

namespace LimbusTranslator.Infrastructure.Diagnostics;

/// <summary>
/// 一次翻译运行的上下文（第4轮）：RunId 在该次运行的所有 Trace 中共享。
///
/// 生命周期：WPF / CLI 在开始一次翻译时创建一个实例，
/// 并把它同时交给 Provider（写 Trace）与 Coordinator（透传）。
/// 禁止每个 Batch 自己生成 RunId。
/// </summary>
public sealed class TranslationRunContext
{
    /// <summary>稳定的运行 Id（同时用作 Trace 文件名）</summary>
    public required string RunId { get; init; }

    /// <summary>创建新的运行上下文</summary>
    public static TranslationRunContext Create()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new TranslationRunContext { RunId = $"{stamp}_{suffix}" };
    }
}

/// <summary>
/// Trace JSONL 写入器（线程安全，最小实现）。
///
/// 约定：
///   - 输出路径：<c>{projectRoot}/logs/translation-trace/{RunId}.jsonl</c>，一行一个条目；
///   - 多个 Agent 并发写入必须串行化，禁止两行 JSON 相互穿插；
///   - 写失败（磁盘满 / 权限 / 目录不可写）**不得导致翻译失败**，只输出 [调试] 日志；
///   - 不写入任何密钥与完整请求/响应内容。
/// </summary>
public sealed class TranslationTraceWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default,
    };

    private readonly string _directory;
    private readonly string? _runId;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disabled;

    public TranslationTraceWriter(string projectRoot, TranslationRunContext run, Action<string>? log = null)
    {
        _directory = Path.Combine(projectRoot, "logs", "translation-trace");
        _runId = run?.RunId;
        _log = log;
    }

    /// <summary>当前运行的文件路径（供日志展示；Trace 关闭时为 null）</summary>
    public string? FilePath => _runId is null ? null : Path.Combine(_directory, _runId + ".jsonl");

    /// <summary>
    /// 追加一条 Trace；任何异常都被吞掉并降级为调试日志。
    /// </summary>
    public void Write(TranslationTraceEntry entry)
    {
        if (entry is null || _disabled || _runId is null)
        {
            return;
        }

        try
        {
            var line = JsonSerializer.Serialize(entry, SerializerOptions);

            _gate.Wait();
            try
            {
                Directory.CreateDirectory(_directory);
                using var stream = new FileStream(
                    FilePath!,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read);
                using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
                writer.WriteLine(line);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            _disabled = true;
            _log?.Invoke($"[调试] Trace写入失败（后续本次运行不再写入）: {Sanitize(ex.Message)}");
        }
    }

    /// <summary>
    /// 错误摘要清洗：限制长度、去掉 Authorization / Bearer / api key 之类敏感片段。
    /// </summary>
    public static string Sanitize(string? message, int maxLength = 300)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var text = message;
        foreach (var keyword in new[] { "Authorization", "authorization", "Bearer", "bearer", "api_key", "apiKey", "sk-" })
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
            {
                text = text.Replace(keyword, "[已脱敏]", StringComparison.Ordinal);
            }
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= maxLength ? text : text[..maxLength] + "...";
    }
}
