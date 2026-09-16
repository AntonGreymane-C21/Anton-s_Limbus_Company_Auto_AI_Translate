using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Paratranz;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第8.89轮：Paratranz 配置契约（默认值 / 启用语义 / 远程并入但不覆盖本地）。</summary>
public sealed class ParatranzConfigContractTests : IDisposable
{
    private readonly string _root;
    private readonly string _configDir;

    public ParatranzConfigContractTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_pconfig_" + Guid.NewGuid().ToString("N")[..8]);
        _configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // 忽略清理失败
        }
    }

    private string CachePath => Path.Combine(_root, "data", "cache", "paratranz_glossary.json");

    private void WriteRemote(params (string Term, string Translation)[] entries)
        => ParatranzGlossaryCacheStore.SaveAtomic(CachePath, new ParatranzGlossaryCache
        {
            ProjectId = "6860",
            FetchedAtUtc = DateTime.UtcNow,
            Entries = entries.Select(e => new ParatranzTermEntry { Term = e.Term, Translation = e.Translation }).ToList(),
        });

    [Fact]
    public void 默认配置_未启用且项目ID为6860()
    {
        var options = AppSettingsLoader.LoadParatranz(_configDir);

        Assert.False(options.Enabled);
        Assert.Equal("6860", options.ProjectId);
    }

    [Fact]
    public void 保存后重新读取_保持启用状态且不破坏其它配置段()
    {
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"),
            "{\n  \"provider\": \"mock\",\n  \"deepSeek\": { \"model\": \"m\" }\n}");

        AppSettingsLoader.SaveParatranz(_configDir, new ParatranzOptions { Enabled = true, ProjectId = "6860" });

        var options = AppSettingsLoader.LoadParatranz(_configDir);
        Assert.True(options.Enabled);
        Assert.Equal("6860", options.ProjectId);

        var raw = File.ReadAllText(Path.Combine(_configDir, "appsettings.json"));
        Assert.Contains("\"provider\": \"mock\"", raw);   // 其它段必须保留
        Assert.Contains("deepSeek", raw);
        Assert.Contains("paratranz", raw);
    }

    [Fact]
    public void 未启用时_远程缓存不参与快照()
    {
        WriteRemote(("Ishmael", "以实玛利"));

        var snapshot = ActiveGlossarySnapshot.LoadWithParatranz(
            _configDir, new ParatranzOptions { Enabled = false, CachePath = CachePath }, out var summary);

        Assert.False(summary.ParatranzEnabled);
        Assert.Empty(snapshot.SelectTerms(new[] { "Ishmael attacks" }));
    }

    [Fact]
    public void 启用且有缓存时_远程术语以Preferred并入()
    {
        WriteRemote(("Ishmael", "以实玛利"));

        var snapshot = ActiveGlossarySnapshot.LoadWithParatranz(
            _configDir, new ParatranzOptions { Enabled = true, CachePath = CachePath }, out var summary);

        Assert.True(summary.ParatranzEnabled);
        Assert.Equal(1, summary.MergedParatranzCount);

        var hit = Assert.Single(snapshot.SelectTerms(new[] { "Ishmael attacks" }));
        Assert.Equal("以实玛利", hit.Value.Translation);
        Assert.False(hit.Value.Locked); // 远程一律 Preferred
        Assert.Equal(GlossaryTermSource.Paratranz, hit.Value.Source);
    }

    [Fact]
    public void 本地术语必须覆盖远程同名()
    {
        // 本地术语库：Gregor → 格里高尔（Locked）
        File.WriteAllText(Path.Combine(_configDir, "glossary.json"),
            "{\n  \"Gregor\": { \"translation\": \"格里高尔\", \"locked\": true }\n}");
        WriteRemote(("Gregor", "格雷戈尔"), ("Ishmael", "以实玛利"));

        var snapshot = ActiveGlossarySnapshot.LoadWithParatranz(
            _configDir, new ParatranzOptions { Enabled = true, CachePath = CachePath }, out var summary);

        var gregor = snapshot.Entries["Gregor"];
        Assert.Equal("格里高尔", gregor.Translation);
        Assert.True(gregor.Locked);
        Assert.Equal(GlossaryTermSource.Local, gregor.Source);

        var conflict = Assert.Single(summary.Conflicts);
        Assert.Equal("格雷戈尔", conflict.RemoteTarget);
        Assert.Equal("格里高尔", conflict.LocalTarget);
        Assert.Equal(GlossaryTermSource.Local, conflict.Winner);

        // 远程新增术语仍可进入快照
        Assert.Equal("以实玛利", snapshot.Entries["Ishmael"].Translation);
    }
}