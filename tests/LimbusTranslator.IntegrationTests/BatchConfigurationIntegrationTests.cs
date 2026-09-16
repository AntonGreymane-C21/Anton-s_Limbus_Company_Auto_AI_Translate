using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.IntegrationTests.Support;

namespace LimbusTranslator.IntegrationTests;

/// <summary>
/// 第7.5轮：batch 配置在全量流水线中的效果（Parser → Diff → Agent → Provider → Cache → Trace → Merge）。
///
/// 证明：
///   1. 分批配置真实决定 Provider 请求组合（默认 vs maxItemsPerBatch=1）；
///   2. 请求组合变化 → 指纹变化 → 旧缓存不会被错误复用；
///   3. 相同配置重复运行 → 全部命中缓存（不再调用 Provider）。
///
/// 使用临时目录 / 临时 SQLite / Fake 客户端，不访问真实 API。
/// </summary>
[Collection(SqliteCollection.Name)]
public class BatchConfigurationIntegrationTests
{
    [Fact]
    public async Task Batch配置变化_请求划分与缓存命中随之变化()
    {
        using var fixture = IntegrationFixture.Create();

        // 第一次：默认分批（20 条 / 30000 字符）→ 两个 Stage 各 1 个请求
        using (var first = PipelineHarness.Create(fixture))
        {
            var run = await first.RunAsync();

            Assert.Equal(2, run.ProviderCalls);
            Assert.Equal(2, first.CountRows("request_cache"));
            first.ClearTranslations();                       // 保留 request_cache，仅清空 TM
        }

        // 第二次：maxItemsPerBatch=1 → 每个条目单独请求（默认夹具共 8 条待翻译）
        using (var second = PipelineHarness.Create(fixture))
        {
            var run = await second.RunAsync(batchOptions: new BatchOptions { MaxItemsPerBatch = 1 });

            Assert.Equal(8, run.ProviderCalls);              // 请求组合改变 → 新指纹 → 全部 Miss
            Assert.Equal(10, second.CountRows("request_cache"));
            second.ClearTranslations();
        }

        // 第三次：配置与第二次完全相同 → 请求完全相同 → 全部命中缓存
        using (var third = PipelineHarness.Create(fixture))
        {
            var run = await third.RunAsync(batchOptions: new BatchOptions { MaxItemsPerBatch = 1 });

            Assert.Equal(0, run.ProviderCalls);
            Assert.Equal(10, third.CountRows("request_cache"));
        }
    }
}
