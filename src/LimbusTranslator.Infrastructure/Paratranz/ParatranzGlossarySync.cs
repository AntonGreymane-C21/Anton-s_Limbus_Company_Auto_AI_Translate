using System.Net;
using System.Text.Json;
using LimbusTranslator.Infrastructure.Security;

namespace LimbusTranslator.Infrastructure.Paratranz;

/// <summary>
/// Paratranz 术语同步（第8.88轮）。
/// **只读远程、只写自己的缓存**，绝不写入 config/glossary.json；分页读取、不无限重试；
/// 远程文本视为不可信（长度/空值过滤、仅 JSON 解析）；公开接口当前**不需要认证**，不内置任何 Token。
/// </summary>
public sealed class ParatranzGlossarySync
{
    private readonly ParatranzOptions _options;
    private readonly HttpMessageHandler? _handler;
    private readonly Action<string>? _log;

    /// <param name="options">配置</param>
    /// <param name="handler">测试注入用 handler（生产为 null）</param>
    /// <param name="log">日志回调</param>
    public ParatranzGlossarySync(ParatranzOptions options, HttpMessageHandler? handler = null, Action<string>? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handler = handler;
        _log = log;
    }

    /// <summary>执行同步（分页读取 → 规范化 → 审计 → 原子写缓存）。</summary>
    public async Task<ParatranzSyncResult> SyncAsync(string cachePath, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return Fail(ParatranzSyncStatus.ConfigError, "Paratranz 未启用（paratranz.enabled=false）。", cachePath);
        }

        if (string.IsNullOrWhiteSpace(_options.ProjectId))
        {
            return Fail(ParatranzSyncStatus.ConfigError, "未配置 paratranz.projectId，已停止同步。", cachePath);
        }

        var timeout = _options.TimeoutSeconds > 0 ? Math.Min(_options.TimeoutSeconds, 120) : 15;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var raw = new List<ParatranzTermEntry>();
        var page = 1;

        try
        {
            using var client = _handler is null
                ? new HttpClient { Timeout = TimeSpan.FromSeconds(timeout) }
                : new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(timeout) };

            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

            while (page <= Math.Max(1, _options.MaxPages))
            {
                var url = $"{_options.BaseUrl.TrimEnd('/')}/projects/{_options.ProjectId}/terms?page={page}&pageSize={_options.PageSize}";
                using var response = await client.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return Fail(StatusMap(response.StatusCode), $"HTTP {(int)response.StatusCode}", cachePath, stopwatch.ElapsedMilliseconds);
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var pageEntries = ParsePage(body, out var parseError);
                if (pageEntries is null)
                {
                    return Fail(ParatranzSyncStatus.FormatError,
                        "接口返回结构无法解析: " + SecretRedactor.Summarize(parseError, 120), cachePath, stopwatch.ElapsedMilliseconds);
                }

                if (pageEntries.Count == 0)
                {
                    break;
                }

                raw.AddRange(pageEntries);
                _log?.Invoke($"[调试] Paratranz 第 {page} 页：{pageEntries.Count} 条（累计 {raw.Count}）");

                if (pageEntries.Count < _options.PageSize)
                {
                    break;
                }

                page++;
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(ParatranzSyncStatus.Timeout, $"请求超时（超过 {timeout} 秒）", cachePath, stopwatch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            return Fail(ParatranzSyncStatus.NetworkError, "网络错误: " + SecretRedactor.Summarize(ex.Message, 160), cachePath, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return Fail(ParatranzSyncStatus.NetworkError, "同步失败: " + SecretRedactor.Summarize(ex.Message, 160), cachePath, stopwatch.ElapsedMilliseconds);
        }

        var (entries, audit) = ParatranzGlossaryCacheStore.Normalize(raw);
        stopwatch.Stop();

        if (entries.Count == 0)
        {
            _log?.Invoke("[调试] Paratranz 远程无有效条目，保留旧缓存（不覆盖）。");
            return new ParatranzSyncResult
            {
                Success = false,
                Status = ParatranzSyncStatus.NoEntries,
                Message = "远程未返回有效条目，已保留上一次缓存（不覆盖）。",
                RawCount = raw.Count,
                Audit = audit,
                KeptPreviousCache = File.Exists(cachePath),
                CachePath = cachePath,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }

        var cache = new ParatranzGlossaryCache
        {
            ProjectId = _options.ProjectId,
            FetchedAtUtc = DateTime.UtcNow,
            Source = $"paratranz:/projects/{_options.ProjectId}/terms",
            Entries = entries,
            Audit = audit,
        };

        try
        {
            ParatranzGlossaryCacheStore.SaveAtomic(cachePath, cache);
        }
        catch (Exception ex)
        {
            return new ParatranzSyncResult
            {
                Success = false,
                Status = ParatranzSyncStatus.FormatError,
                Message = "缓存写入失败（已保留旧缓存）: " + SecretRedactor.Summarize(ex.Message, 160),
                RawCount = raw.Count,
                EffectiveCount = entries.Count,
                Audit = audit,
                KeptPreviousCache = true,
                CachePath = cachePath,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }

        _log?.Invoke(
            $"[调试] Paratranz 同步成功：原始 {raw.Count} 条 / 有效 {entries.Count} 条 / " +
            $"同源多译 {audit.MultiTargetConflicts} 组 / 丢弃重复 {audit.DroppedDuplicate} / 韩文残留 {audit.DroppedHangulTarget}");

        return new ParatranzSyncResult
        {
            Success = true,
            Status = ParatranzSyncStatus.Success,
            Message = "同步成功",
            RawCount = raw.Count,
            EffectiveCount = entries.Count,
            Audit = audit,
            CachePath = cachePath,
            DurationMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static ParatranzSyncStatus StatusMap(HttpStatusCode status) => (int)status switch
    {
        401 or 403 or 404 => ParatranzSyncStatus.ConfigError,
        _ => ParatranzSyncStatus.HttpError,
    };

    private ParatranzSyncResult Fail(ParatranzSyncStatus status, string message, string cachePath, long durationMs = 0)
    {
        _log?.Invoke($"[调试] Paratranz 同步失败（{status}）: {message}（保留旧缓存）");
        return new ParatranzSyncResult
        {
            Success = false,
            Status = status,
            Message = message,
            KeptPreviousCache = File.Exists(cachePath),
            CachePath = cachePath,
            DurationMs = durationMs,
        };
    }

    /// <summary>
    /// 容错解析一页：兼容 数组 / data / results / terms 包装，以及 term·source·original·key 与
    /// translation·target·value·translated 等字段名。无法识别 → null（转为 FormatError，不破坏旧缓存）。
    /// </summary>
    private static List<ParatranzTermEntry>? ParsePage(string body, out string parseError)
    {
        parseError = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            JsonElement array;
            if (root.ValueKind == JsonValueKind.Array)
            {
                array = root;
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryGet(root, "data", out array) || TryGet(root, "results", out array) || TryGet(root, "terms", out array))
                {
                    if (array.ValueKind != JsonValueKind.Array)
                    {
                        parseError = "包装字段不是数组";
                        return null;
                    }
                }
                else
                {
                    parseError = "顶层既不是数组也没有 data/results/terms 字段";
                    return null;
                }
            }
            else
            {
                parseError = "顶层既不是数组也不是对象";
                return null;
            }

            var list = new List<ParatranzTermEntry>();
            foreach (var item in array.EnumerateArray())
            {
                var term = FirstString(item, "term", "source", "original", "key");
                var target = FirstString(item, "translation", "target", "value", "translated");

                if (term is null && item.ValueKind == JsonValueKind.Object
                    && TryGet(item, "term", out var nested) && nested.ValueKind == JsonValueKind.Object)
                {
                    term = FirstString(nested, "term", "source", "original");
                    target ??= FirstString(nested, "translation", "target", "value");
                }

                int? remoteId = null;
                if (item.ValueKind == JsonValueKind.Object && TryGet(item, "id", out var idElement) && idElement.TryGetInt32(out var id))
                {
                    remoteId = id;
                }

                var updatedAt = item.ValueKind == JsonValueKind.Object && TryGet(item, "updatedAt", out var updated) && updated.ValueKind == JsonValueKind.String
                    ? updated.GetString()
                    : null;

                list.Add(new ParatranzTermEntry
                {
                    Term = term ?? string.Empty,
                    Translation = target ?? string.Empty,
                    RemoteId = remoteId,
                    UpdatedAt = updatedAt,
                });
            }

            return list;
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
            return null;
        }
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? FirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }
}
