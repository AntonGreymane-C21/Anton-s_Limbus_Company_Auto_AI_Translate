using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第7轮（T-7）：DeepSeekTranslationProvider 生命周期测试。
///
/// 修复前：类里有 Dispose() 方法但没有声明 IDisposable，
/// 因此 WPF / CLI 的 <c>provider as IDisposable</c> 恒为 null，限流器与自建 HttpClient 永远不会被释放。
///
/// Ownership 约定：
///   - 注入的客户端（测试 Fake / 共享实例）由调用方释放；
///   - Provider 自己创建的客户端与 SemaphoreSlim 由 Provider 释放。
/// </summary>
public class ProviderLifecycleTests
{
    /// <summary>可观察 Dispose 次数的假客户端（不访问网络）。</summary>
    private sealed class DisposableFakeClient : IDeepSeekBatchClient, IDisposable
    {
        public int DisposeCount { get; private set; }

        public int CallCount { get; private set; }

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
        {
            CallCount++;
            var results = items.ToDictionary(
                i => i.Id,
                i => new DeepSeekTranslateItem(i.Id, "[译]" + i.Source, false, string.Empty),
                StringComparer.Ordinal);
            return Task.FromResult(new DeepSeekBatchResult { Items = results });
        }

        public void Dispose() => DisposeCount++;
    }

    private static DeepSeekOptions MakeOptions() => new()
    {
        ApiKey = "sk-lifecycle-test",
        ApiUrl = "https://api.deepseek.com/chat/completions",
        Model = "lifecycle-model",
        MaxRetry = 0,
    };

    private static DiffEntry MakeEntry(string text)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[0].name" },
            NewSourceText = text,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };

    [Fact]
    public void Provider可通过IDisposable识别()
    {
        var provider = new DeepSeekTranslationProvider(MakeOptions(), client: new DisposableFakeClient());

        // WPF / CLI 使用的写法：provider as IDisposable
        Assert.NotNull(provider as IDisposable);
        Assert.IsAssignableFrom<IDisposable>(provider);

        provider.Dispose();
    }

    [Fact]
    public void Dispose可重复调用且不抛异常()
    {
        var provider = new DeepSeekTranslationProvider(MakeOptions(), client: new DisposableFakeClient());

        provider.Dispose();
        provider.Dispose();
        provider.Dispose();
    }

    [Fact]
    public void 注入的客户端不会被Provider释放()
    {
        var client = new DisposableFakeClient();
        var provider = new DeepSeekTranslationProvider(MakeOptions(), client: client);

        provider.Dispose();

        Assert.Equal(0, client.DisposeCount);   // 共享实例由调用方负责
    }

    [Fact]
    public async Task Dispose后再次翻译_抛出ObjectDisposedException()
    {
        var provider = new DeepSeekTranslationProvider(MakeOptions(), client: new DisposableFakeClient());
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => provider.TranslateAsync(new[] { MakeEntry("Real text.") }));
    }

    [Fact]
    public async Task 释放后新建第二个Provider_仍可正常运行()
    {
        var client = new DisposableFakeClient();

        var first = new DeepSeekTranslationProvider(MakeOptions(), client: client);
        first.Dispose();

        var second = new DeepSeekTranslationProvider(MakeOptions(), client: client);
        var results = await second.TranslateAsync(new[] { MakeEntry("Second run text.") });
        second.Dispose();

        Assert.Single(results);
        Assert.Equal("[译]Second run text.", results.Values.Single().Translation);
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public void 未注入客户端时_Provider释放自建资源不抛异常()
    {
        // 未注入 → Provider 自己创建 DeepSeekClient（含 HttpClient），由 Provider 释放
        var provider = new DeepSeekTranslationProvider(MakeOptions());

        provider.Dispose();
        provider.Dispose();
    }
}
