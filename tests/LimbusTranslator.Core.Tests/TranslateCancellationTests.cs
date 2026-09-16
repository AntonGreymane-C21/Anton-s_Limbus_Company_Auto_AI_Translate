using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Translation;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.1轮：**翻译取消**（Translate Cancel）自动化验收。
///
/// 目标：取消后不再发送新的 Provider 请求；已发出的请求允许安全返回；TEMP TM 不被破坏。
/// 使用确定性同步（TaskCompletionSource）而非 Thread.Sleep 猜测时序。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class TranslateCancellationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "limbus_translate_cancel_" + Guid.NewGuid().ToString("N")[..8]);

    public TranslateCancellationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言
        }
    }

    [Fact]
    public async Task 取消翻译后不再发送新的Provider请求且TM保持可用()
    {
        var entries = BuildEntries(stageCount: 5);
        var provider = new GatedCountingProvider();
        var dbPath = Path.Combine(_root, "translation_memory.db");
        using var memory = new SqliteTranslationMemory(new TranslationMemoryOptions { DatabasePath = dbPath });

        var coordinator = new Coordinator(
            provider,
            memory,
            maxConcurrentAgents: 1,
            maxConcurrentApiRequests: 1);

        using var cts = new CancellationTokenSource();
        var run = coordinator.ExecuteAsync(entries, null, cts.Token);

        // 第一次 Provider 调用已经进入（确定性等待，不依赖 sleep 猜测）
        await provider.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        provider.ReleaseFirstCall();     // 模拟“已发出的请求安全返回”

        try
        {
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (OperationCanceledException)
        {
            // 取消导致的异常同样属于安全停止
        }

        // ① 取消之后没有再发送新的请求（5 个 Stage 只发出 1 次）
        Assert.Equal(1, provider.CallCount);

        // ② TEMP TM 未被破坏：仍可正常打开并查询
        using var connection = new SqliteConnection($"Data Source={dbPath.Replace("\\", "/")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM translations;";
        _ = Convert.ToInt32(command.ExecuteScalar());
    }

    [Fact]
    public async Task 没有待翻译条目时不会调用Provider()
    {
        var provider = new GatedCountingProvider();
        var dbPath = Path.Combine(_root, "empty.db");
        using var memory = new SqliteTranslationMemory(new TranslationMemoryOptions { DatabasePath = dbPath });
        var coordinator = new Coordinator(provider, memory, maxConcurrentAgents: 1, maxConcurrentApiRequests: 1);

        var result = await coordinator.ExecuteAsync(Array.Empty<DiffEntry>());

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, result.TotalTranslated);
    }

    private static IReadOnlyList<DiffEntry> BuildEntries(int stageCount)
        => Enumerable.Range(1, stageCount)
            .Select(index => new DiffEntry
            {
                Key = new UnitKey
                {
                    RelativeFilePath = $"File{index}.json",
                    RecordId = index.ToString(),
                    FieldPath = "dataList[0].name",
                },
                NewSourceText = $"Line {index}",
                DiffKind = DiffKind.Added,
                Action = TranslationAction.TranslateNew,
            })
            .ToList();

    /// <summary>第一次调用阻塞在 TCS 上的 Provider（记录调用次数；返回后可继续）。</summary>
    private sealed class GatedCountingProvider : ITranslationProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _callCount;

        public TaskCompletionSource FirstCallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public void ReleaseFirstCall() => _release.TrySetResult();

        public async Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
            IReadOnlyList<DiffEntry> entries,
            CancellationToken cancellationToken = default,
            string? stageId = null,
            IReadOnlyDictionary<string, TranslationContext>? contexts = null)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
            {
                FirstCallEntered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var results = entries.ToDictionary(
                entry => entry.Key.ToString(),
                entry => new TranslationResult
                {
                    Key = entry.Key,
                    Translation = $"译文 {entry.NewSourceText}",
                },
                StringComparer.Ordinal);
            return results;
        }
    }
}
