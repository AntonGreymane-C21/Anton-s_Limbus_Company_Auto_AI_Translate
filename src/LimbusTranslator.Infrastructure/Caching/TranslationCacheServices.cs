using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Infrastructure.Diagnostics;

namespace LimbusTranslator.Infrastructure.Caching;

/// <summary>
/// Provider 缓存 / Trace 服务集合（第4轮）。
///
/// 由 WPF / CLI 在每次翻译运行时创建一次，并注入：
///   - <see cref="DeepSeekTranslationProvider"/>（按请求查缓存、写指纹、写 Trace）；
///   - <see cref="Translation.TranslationAgent"/>（校验后 Flush / Discard 暂存项）。
///
/// 全部字段可空：为空表示该能力关闭（例如只跑 Trace、或测试中完全关闭缓存）。
/// </summary>
public sealed class TranslationCacheServices
{
    private int _requestSequence;

    /// <summary>请求缓存（null = 关闭缓存）</summary>
    public IRequestCache? Cache { get; init; }

    /// <summary>缓存暂存区（与 <see cref="Cache"/> 同时存在）</summary>
    public RequestCacheStaging? Staging { get; init; }

    /// <summary>Trace 写入器（null = 关闭 Trace）</summary>
    public TranslationTraceWriter? Trace { get; init; }

    /// <summary>本次运行上下文（RunId）</summary>
    public TranslationRunContext? Run { get; init; }

    /// <summary>生成逻辑请求 Id（同一次请求的多次重试共享同一个 Id）</summary>
    public string NextRequestId() => $"req-{Interlocked.Increment(ref _requestSequence):D5}";

    /// <summary>创建一套完整的缓存 + Trace 服务。</summary>
    public static TranslationCacheServices Create(
        IRequestCache? cache,
        TranslationRunContext? run,
        TranslationTraceWriter? trace = null,
        Action<string>? log = null)
        => new()
        {
            Cache = cache,
            Staging = cache is null ? null : new RequestCacheStaging(cache, log),
            Trace = trace,
            Run = run,
        };
}
