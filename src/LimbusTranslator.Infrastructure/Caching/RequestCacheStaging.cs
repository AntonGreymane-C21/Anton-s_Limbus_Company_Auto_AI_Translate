using System.Collections.Concurrent;
using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Caching;

/// <summary>
/// 缓存暂存区（第4轮引入，第5轮修正并发隔离）：
///   Provider 命中网络后先 <see cref="Stage"/>（只暂存，不落库）；
///   TranslationAgent 完成当前 Validator 后：
///       - 该请求出现 HardSafety Error → <see cref="Discard"/>（不写可命中缓存）；
///       - 其余 → <see cref="Flush"/> 落库。
///
/// 【并发隔离规则（第5轮修复）】
///   暂存与提交都按 **RequestId** 隔离：
///     - 每个逻辑 Provider 请求（RequestId 与 Fingerprint 一一对应）单独成项；
///     - Flush(requestIds) 只提交调用方自己那一批请求，绝不会把其它 Agent 尚未校验的请求写进缓存；
///     - Discard(requestId) 只丢弃自己那一批请求，也不会影响别人的暂存状态；
///     - 未被 Flush / Discard 的请求留在暂存区（不落库），随本次运行的服务实例一起释放。
///
/// 修复前：Flush()（无参）会提交全部暂存项，可能让 A 的未校验响应“搭车”被 B 的 Flush 写入缓存，
/// 导致 A 后续的 Discard 失效（坏响应被缓存）。
/// </summary>
public sealed class RequestCacheStaging
{
    private sealed record PendingRequest(string Fingerprint, CachedProviderBatchResponse Response);

    private readonly IRequestCache? _cache;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, PendingRequest> _pending = new(StringComparer.Ordinal);

    public RequestCacheStaging(IRequestCache? cache, Action<string>? log = null)
    {
        _cache = cache;
        _log = log;
    }

    /// <summary>暂存中的请求数量</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// 暂存一次成功响应（按 RequestId 隔离；同 RequestId 重复暂存时以最后一次为准）。
    /// </summary>
    public void Stage(string? requestId, string fingerprint, CachedProviderBatchResponse response)
    {
        if (_cache is null
            || string.IsNullOrWhiteSpace(requestId)
            || string.IsNullOrWhiteSpace(fingerprint)
            || response is null)
        {
            return;
        }

        _pending[requestId] = new PendingRequest(fingerprint, response);
    }

    /// <summary>丢弃某个请求的暂存项（校验发现硬安全问题，或该请求不应被缓存）。</summary>
    public void Discard(string? requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        if (_pending.TryRemove(requestId, out var pending))
        {
            _log?.Invoke(
                $"[调试] RequestCache Skip: Fingerprint={Shorten(pending.Fingerprint)}（校验发现硬安全问题，不写入缓存）");
        }
    }

    /// <summary>
    /// 只提交指定请求的暂存项（生产路径使用），返回写入数量。
    /// 其它请求的暂存状态完全不受影响。
    /// </summary>
    public int Flush(IEnumerable<string?> requestIds)
    {
        if (_cache is null || requestIds is null)
        {
            return 0;
        }

        var saved = 0;
        foreach (var requestId in requestIds.Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(requestId) || !_pending.TryRemove(requestId, out var pending))
            {
                continue;
            }

            saved += SaveRequest(pending);
        }

        return saved;
    }

    /// <summary>
    /// 提交全部暂存项（仅供测试 / 运行收尾使用；生产路径请使用 <see cref="Flush"/>）。
    /// </summary>
    public int FlushAll()
    {
        if (_cache is null || _pending.IsEmpty)
        {
            return 0;
        }

        var saved = 0;
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                saved += SaveRequest(pending);
            }
        }

        return saved;
    }

    private int SaveRequest(PendingRequest pending)
    {
        try
        {
            _cache!.Save(pending.Fingerprint, pending.Response);
            _log?.Invoke($"[调试] RequestCache Save: Fingerprint={Shorten(pending.Fingerprint)}");
            return 1;
        }
        catch (Exception ex)
        {
            // 并发写入冲突 / 磁盘问题都不得让翻译失败
            _log?.Invoke(
                $"[调试] RequestCache Save 失败（忽略）: Fingerprint={Shorten(pending.Fingerprint)} - {ex.Message}");
            return 0;
        }
    }

    private static string Shorten(string fingerprint)
        => fingerprint.Length <= 12 ? fingerprint : fingerprint[..12];
}
