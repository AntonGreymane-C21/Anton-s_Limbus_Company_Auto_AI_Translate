using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Core.Abstractions;

/// <summary>
/// Provider 请求缓存（第4轮）。
///
/// 语义边界（非常重要）：
///   Translation Memory = 已确认某个 UnitKey + SourceHash 直接得到什么译文；
///   Request Cache      = 某一次完全相同的 Provider 请求，是否已经得到过完全可重放的 Provider 响应。
/// 两者禁止混为一谈：TM 命中（ExactUnit）不会进入本缓存。
///
/// 实现约定：
///   1. 读取时遇到坏数据（JSON 损坏 / 版本不支持 / 结构不一致）必须降级为 Miss，禁止抛异常；
///   2. 写入时同一 Fingerprint 只保留一条（并发重复写入不得导致失败）。
/// </summary>
public interface IRequestCache
{
    /// <summary>
    /// 按指纹读取缓存响应；不存在或数据不可用时返回 null（调用方视为 Cache Miss）。
    /// </summary>
    CachedProviderBatchResponse? TryGet(string fingerprint);

    /// <summary>
    /// 写入缓存响应（同一指纹重复写入应安全覆盖）。
    /// </summary>
    void Save(string fingerprint, CachedProviderBatchResponse response);
}
