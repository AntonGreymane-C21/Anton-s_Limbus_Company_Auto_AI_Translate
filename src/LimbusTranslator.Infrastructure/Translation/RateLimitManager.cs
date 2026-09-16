namespace LimbusTranslator.Infrastructure.Translation;

/// <summary>
/// RateLimit 管理器（文档 §12）。
///
/// 区分两个概念：
///   MaxConcurrentAgents       同时运行多少个 Stage（关卡）
///   MaxConcurrentApiRequests  整个程序同时允许多少个 DeepSeek HTTP 请求
///
/// 所有 TranslationAgent 在调用 API 前必须先 AcquireApiSlot。
/// </summary>
public sealed class RateLimitManager
{
    private readonly SemaphoreSlim _agentSemaphore;
    private readonly SemaphoreSlim _apiSemaphore;

    public RateLimitManager(int maxConcurrentAgents, int maxConcurrentApiRequests)
    {
        _agentSemaphore = new SemaphoreSlim(Math.Max(1, maxConcurrentAgents));
        _apiSemaphore = new SemaphoreSlim(Math.Max(1, maxConcurrentApiRequests));
    }

    /// <summary>
    /// 最大并发 Agent 数。
    /// </summary>
    public int GetMaxConcurrentAgents() => _agentSemaphore.CurrentCount;

    /// <summary>
    /// 获取一个 Agent 并发名额。
    /// </summary>
    public async Task<IDisposable> AcquireAgentSlotAsync(CancellationToken ct)
    {
        await _agentSemaphore.WaitAsync(ct);
        return new Releaser(_agentSemaphore);
    }

    /// <summary>
    /// 获取一个 API 请求名额。
    /// </summary>
    public async Task<IDisposable> AcquireApiSlotAsync(CancellationToken ct)
    {
        await _apiSemaphore.WaitAsync(ct);
        return new Releaser(_apiSemaphore);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private bool _released;

        public Releaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (!_released)
            {
                _semaphore.Release();
                _released = true;
            }
        }
    }
}
