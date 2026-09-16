using System.Net;
using LimbusTranslator.Infrastructure.Paratranz;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第8.88轮：Paratranz 同步（Fake HTTP；不依赖真实远程服务）。</summary>
[Collection(SqliteCollection.Name)]
public sealed class ParatranzTests : IDisposable
{
    private readonly string _dir;

    public ParatranzTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "limbus_paratranz_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, true);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private string CachePath => Path.Combine(_dir, "paratranz_glossary.json");

    private static ParatranzOptions Options(int pageSize = 3) => new()
    {
        Enabled = true,
        ProjectId = "12345",
        BaseUrl = "https://paratranz.test/api",
        PageSize = pageSize,
        TimeoutSeconds = 5,
    };

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<int, (HttpStatusCode Status, string Body)> _responder;

        public FakeHandler(Func<int, (HttpStatusCode, string)> responder) => _responder = responder;

        public List<string> Urls { get; } = new();

        public List<string?> AuthHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            AuthHeaders.Add(request.Headers.Authorization?.ToString());
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
            var page = int.TryParse(query["page"], out var p) ? p : 1;
            var (status, body) = _responder(page);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static string Terms(params (string Term, string Translation)[] entries)
        => "[" + string.Join(",", entries.Select(e => $"{{\"term\":\"{e.Term}\",\"translation\":\"{e.Translation}\"}}")) + "]";

    [Fact]
    public async Task 分页合并_应写入缓存且请求不携带凭据()
    {
        var handler = new FakeHandler(page => page switch
        {
            1 => (HttpStatusCode.OK, Terms(("Sinclair", "辛克莱"), ("Gregor", "格里高尔"), ("Haste", "迅捷"))),
            2 => (HttpStatusCode.OK, Terms(("Bleed", "流血"))),
            _ => (HttpStatusCode.OK, "[]"),
        });

        var result = await new ParatranzGlossarySync(Options(pageSize: 3), handler).SyncAsync(CachePath);

        Assert.True(result.Success);
        Assert.Equal(4, result.EffectiveCount);
        Assert.Contains("page=2", handler.Urls[1]);
        Assert.All(handler.AuthHeaders, h => Assert.Null(h));

        var cache = ParatranzGlossaryCacheStore.TryLoad(CachePath);
        Assert.NotNull(cache);
        Assert.Equal(4, cache!.Entries.Count);
        Assert.Equal("12345", cache.ProjectId);
        Assert.False(File.Exists(CachePath + ".tmp"));
    }

    [Fact]
    public async Task 规范化_过滤空值重复并统计同源多译()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK,
            "[{\"term\":\"Ring\",\"translation\":\"环\"}," +
            "{\"term\":\"Ring\",\"translation\":\"环\"}," +
            "{\"term\":\"Bleed\",\"translation\":\"\"}," +
            "{\"term\":\"\",\"translation\":\"空\"}," +
            "{\"term\":\"Haste\",\"translation\":\"迅捷\"}," +
            "{\"term\":\"Haste\",\"translation\":\"急速\"}]"));

        var result = await new ParatranzGlossarySync(Options(pageSize: 50), handler).SyncAsync(CachePath);

        Assert.True(result.Success);
        Assert.Equal(3, result.EffectiveCount);
        Assert.Equal(1, result.Audit.DroppedDuplicate);
        Assert.Equal(1, result.Audit.DroppedEmptyTarget);
        Assert.Equal(1, result.Audit.DroppedEmptySource);
        Assert.Equal(1, result.Audit.MultiTargetConflicts);
    }

    [Fact]
    public async Task 同步失败_必须保留旧缓存()
    {
        var ok = new FakeHandler(_ => (HttpStatusCode.OK, Terms(("Ring", "环"))));
        Assert.True((await new ParatranzGlossarySync(Options(pageSize: 50), ok).SyncAsync(CachePath)).Success);
        var before = File.ReadAllText(CachePath);

        var failing = new FakeHandler(_ => (HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}"));
        var second = await new ParatranzGlossarySync(Options(pageSize: 50), failing).SyncAsync(CachePath);

        Assert.False(second.Success);
        Assert.Equal(ParatranzSyncStatus.HttpError, second.Status);
        Assert.True(second.KeptPreviousCache);
        Assert.Equal(before, File.ReadAllText(CachePath));
    }

    [Fact]
    public async Task 网络异常与解析失败_应映射到对应状态且不留下临时文件()
    {
        var network = await new ParatranzGlossarySync(Options(), new ThrowingHandler()).SyncAsync(CachePath);
        Assert.Equal(ParatranzSyncStatus.NetworkError, network.Status);

        var badJson = new FakeHandler(_ => (HttpStatusCode.OK, "{\"unexpected\":true}"));
        var format = await new ParatranzGlossarySync(Options(), badJson).SyncAsync(CachePath);
        Assert.Equal(ParatranzSyncStatus.FormatError, format.Status);

        Assert.False(File.Exists(CachePath + ".tmp"));
    }

    [Fact]
    public async Task 无有效条目_不得覆盖旧缓存()
    {
        var ok = new FakeHandler(_ => (HttpStatusCode.OK, Terms(("Ring", "环"))));
        await new ParatranzGlossarySync(Options(pageSize: 50), ok).SyncAsync(CachePath);
        var before = File.ReadAllText(CachePath);

        var empty = new FakeHandler(_ => (HttpStatusCode.OK, "[]"));
        var result = await new ParatranzGlossarySync(Options(pageSize: 50), empty).SyncAsync(CachePath);

        Assert.Equal(ParatranzSyncStatus.NoEntries, result.Status);
        Assert.Equal(before, File.ReadAllText(CachePath));
    }

    [Fact]
    public async Task 未启用或缺少项目ID_应返回ConfigError且不发请求()
    {
        var handler = new FakeHandler(_ => (HttpStatusCode.OK, "[]"));

        var disabled = await new ParatranzGlossarySync(new ParatranzOptions { Enabled = false }, handler).SyncAsync(CachePath);
        Assert.Equal(ParatranzSyncStatus.ConfigError, disabled.Status);

        var noProject = await new ParatranzGlossarySync(
            new ParatranzOptions { Enabled = true, ProjectId = null }, handler).SyncAsync(CachePath);
        Assert.Equal(ParatranzSyncStatus.ConfigError, noProject.Status);

        Assert.Empty(handler.Urls);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection refused");
    }
}
