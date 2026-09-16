using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Security;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.87轮：术语链路（glossary.json → 匹配 → Prompt → 真实请求 JSON）与保存 Round-trip。
///
/// 背景（真实 Bug）：GUI 保存术语库时写出 PascalCase，而加载只认 camelCase，
/// 导致保存一次后整库译名读成空串 —— 表现为「术语表不生效」「条目只有原文、译文为空」。
/// 本文件锁定修复后的行为，防止回归。
/// </summary>
public sealed class GlossaryChainTests : IDisposable
{
    private readonly string _configDir;

    public GlossaryChainTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "limbus_glossary_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_configDir))
            {
                Directory.Delete(_configDir, true);
            }
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    private string GlossaryPath => Path.Combine(_configDir, "glossary.json");

    private void WriteRaw(string json) => File.WriteAllText(GlossaryPath, json);

    [Fact]
    public void camelCase格式_应正常加载()
    {
        WriteRaw("{\n  \"TestTermAlpha\": { \"translation\": \"测试术语甲\", \"locked\": true }\n}");

        var glossary = new GlossaryService(_configDir);

        Assert.Equal(1, glossary.Count);
        Assert.Empty(glossary.EmptyTranslationKeys);
        Assert.Equal("测试术语甲", glossary.Entries["TestTermAlpha"].Translation);
        Assert.True(glossary.Entries["TestTermAlpha"].Locked);
    }

    [Fact]
    public void PascalCase格式_也应能加载_不再出现整库空译名()
    {
        WriteRaw("{\n  \"TestTermAlpha\": { \"Translation\": \"测试术语甲\", \"Locked\": true }\n}");

        var glossary = new GlossaryService(_configDir);

        Assert.Equal("测试术语甲", glossary.Entries["TestTermAlpha"].Translation);
        Assert.True(glossary.Entries["TestTermAlpha"].Locked);
        Assert.Empty(glossary.EmptyTranslationKeys);
    }

    [Fact]
    public void 单条保存_重新加载应完全一致()
    {
        var glossary = new GlossaryService(_configDir);
        Assert.True(glossary.Update("Philip", "菲利普", locked: true));
        glossary.Save();

        var reloaded = new GlossaryService(_configDir);
        Assert.Equal("菲利普", reloaded.Entries["Philip"].Translation);
        Assert.True(reloaded.Entries["Philip"].Locked);

        var text = File.ReadAllText(GlossaryPath);
        Assert.Contains("\"translation\"", text);
        Assert.DoesNotContain("\"Translation\"", text);
    }

    [Fact]
    public void 多条与Unicode与标点_保存后不得丢失()
    {
        var glossary = new GlossaryService(_configDir);
        glossary.Update("Philip", "菲利普", true);
        glossary.Update("Sinclair", "辛克莱", true);
        glossary.Update("Gregor", "格里高尔", true);
        glossary.Update("싱클레어", "辛克莱", true);
        glossary.Update("E.G.O", "E.G.O", true);
        glossary.Save();

        var reloaded = new GlossaryService(_configDir);
        Assert.Equal(5, reloaded.Count);
        Assert.Equal("菲利普", reloaded.Entries["Philip"].Translation);
        Assert.Equal("辛克莱", reloaded.Entries["Sinclair"].Translation);
        Assert.Equal("格里高尔", reloaded.Entries["Gregor"].Translation);
        Assert.Equal("辛克莱", reloaded.Entries["싱클레어"].Translation);
        Assert.Equal("E.G.O", reloaded.Entries["E.G.O"].Translation);
    }

    [Fact]
    public void 空译名_Update应拒绝且Save不得写出空译名()
    {
        var glossary = new GlossaryService(_configDir);
        glossary.Update("Good", "好的", true);

        Assert.False(glossary.Update("EmptyTarget", "", true));
        Assert.False(glossary.Update("BlankTarget", "   ", true));
        Assert.False(glossary.Update("", "有译名但无原文", true));
        Assert.DoesNotContain("EmptyTarget", glossary.Entries.Keys);

        glossary.Save();
        var text = File.ReadAllText(GlossaryPath);
        Assert.DoesNotContain("EmptyTarget", text);
        Assert.DoesNotContain("\"translation\": \"\"", text);
        Assert.False(File.Exists(GlossaryPath + ".tmp"));
    }

    [Fact]
    public void 提示词_Locked与Preferred应使用不同措辞()
    {
        var entries = new List<KeyValuePair<string, GlossaryEntry>>
        {
            new("Philip", new GlossaryEntry { Translation = "菲利普", Locked = true }),
            new("Move-in Reg.", new GlossaryEntry { Translation = "迁入登记", Locked = false }),
        };

        var prompt = GlossaryService.BuildGlossaryPrompt(entries);

        Assert.Contains("【强制术语（Locked）】", prompt);
        Assert.Contains("Philip → 菲利普", prompt);
        Assert.Contains("【优先术语（参考）】", prompt);
        Assert.Contains("Move-in Reg. → 迁入登记", prompt);
    }

    [Fact]
    public void 命中摘要_应标注Locked状态()
    {
        var entries = new List<KeyValuePair<string, GlossaryEntry>>
        {
            new("Sinclair", new GlossaryEntry { Translation = "辛克莱", Locked = true }),
            new("Move-in Reg.", new GlossaryEntry { Translation = "迁入登记", Locked = false }),
        };

        var text = GlossaryService.DescribeSubset(entries);

        Assert.Contains("Sinclair→辛克莱[L]", text);
        Assert.Contains("Move-in Reg.→迁入登记", text);
        Assert.DoesNotContain("迁入登记[L]", text);
    }

    // ── 8. 完整链路：glossary.json → 匹配 → Prompt → 真实 HTTP 请求体 ────

    [Fact]
    public async Task 术语命中_必须进入Prompt并保留到HTTP请求体()
    {
        WriteRaw("{\n  \"TestTermAlpha\": { \"translation\": \"测试术语甲\", \"locked\": true }\n}");

        var client = new RecordingBatchClient();
        var options = MakeOptions();
        var provider = new DeepSeekTranslationProvider(
            options, configDir: _configDir, cacheServices: null, client: client);

        var entry = MakeEntry("TestTermAlpha attacks the enemy.");
        var results = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");

        Assert.Equal(1, client.CallCount);
        Assert.NotNull(client.LastGlossaryPrompt);
        Assert.Contains("TestTermAlpha", client.LastGlossaryPrompt!);
        Assert.Contains("测试术语甲", client.LastGlossaryPrompt!);
        Assert.Contains("【强制术语（Locked）】", client.LastGlossaryPrompt!);

        // 真实发送给模型的请求体里必须保留术语（Prompt 注入链的最后一环）
        var body = DeepSeekRequestComposer.BuildRequestBodyJson(
            options, new PromptOptions(), "B1", Array.Empty<DeepSeekTranslateRequestItem>(),
            client.LastGlossaryPrompt!, string.Empty, new DeepSeekRequestThinking(false, null));

        using var doc = JsonDocument.Parse(body);
        var systemPrompt = doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
        Assert.NotNull(systemPrompt);
        Assert.Contains("TestTermAlpha", systemPrompt!);
        Assert.Contains("测试术语甲", systemPrompt!);

        Assert.False(string.IsNullOrWhiteSpace(results[entry.Key.ToString()].RequestFingerprint));
    }

    // ── 9. 术语译名变化 → 指纹变化（旧缓存不得错误复用） ──────────────────

    [Fact]
    public async Task 术语译名变化_请求指纹必须变化()
    {
        WriteRaw("{\n  \"TestTermAlpha\": { \"translation\": \"测试术语甲\", \"locked\": true }\n}");
        var options = MakeOptions();

        var first = new RecordingBatchClient();
        var providerA = new DeepSeekTranslationProvider(options, _configDir, null, client: first);
        var entryA = MakeEntry("TestTermAlpha attacks the enemy.");
        var resultA = await providerA.TranslateAsync(new[] { entryA }, CancellationToken.None, "Test.json");

        WriteRaw("{\n  \"TestTermAlpha\": { \"translation\": \"测试术语乙\", \"locked\": true }\n}");

        var second = new RecordingBatchClient();
        var providerB = new DeepSeekTranslationProvider(options, _configDir, null, client: second);
        var entryB = MakeEntry("TestTermAlpha attacks the enemy.");
        var resultB = await providerB.TranslateAsync(new[] { entryB }, CancellationToken.None, "Test.json");

        Assert.NotEqual(
            resultA[entryA.Key.ToString()].RequestFingerprint,
            resultB[entryB.Key.ToString()].RequestFingerprint);
    }

    // ── 10. 旧译文规则 / 分类指令进入真实请求（默认不进入，保持缓存兼容） ─

    [Fact]
    public void 旧译文规则与分类指令_应进入请求体且默认不进入()
    {
        var options = MakeOptions();
        var prompt = new PromptOptions();

        var plain = DeepSeekRequestComposer.BuildRequestBodyJson(
            options, prompt, "B1", Array.Empty<DeepSeekTranslateRequestItem>(), string.Empty, string.Empty);
        Assert.DoesNotContain("OldSource", SystemPromptOf(plain));
        Assert.DoesNotContain("剧情内容", SystemPromptOf(plain));

        var enriched = DeepSeekRequestComposer.BuildRequestBodyJson(
            options, prompt, "B1", Array.Empty<DeepSeekTranslateRequestItem>(), string.Empty, string.Empty,
            new DeepSeekRequestThinking(false, null), includeModifiedRule: true, category: TextCategory.StoryData);

        Assert.Contains("OldSource", SystemPromptOf(enriched));
        Assert.Contains("剧情内容", SystemPromptOf(enriched));
    }

    /// <summary>从请求体 JSON 中取出 system 消息内容（JSON 默认会转义非 ASCII，因此必须解析后断言）。</summary>
    private static string SystemPromptOf(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString() ?? string.Empty;
    }

    // ── 11. 命中术语摘要进入 Trace（诊断「术语表是否真的注入」） ───────────

    [Fact]
    public async Task 命中术语摘要_应写入Trace()
    {
        WriteRaw("{\n  \"TestTermAlpha\": { \"translation\": \"测试术语甲\", \"locked\": true }\n}");

        var traceRoot = Path.Combine(_configDir, "trace_root");
        Directory.CreateDirectory(traceRoot);
        var run = TranslationRunContext.Create();
        var writer = new TranslationTraceWriter(traceRoot, run);
        var services = TranslationCacheServices.Create(cache: null, run: run, trace: writer);

        var client = new RecordingBatchClient();
        var provider = new DeepSeekTranslationProvider(MakeOptions(), _configDir, null, services, client);
        var entry = MakeEntry("TestTermAlpha attacks the enemy.");
        await provider.TranslateAsync(new[] { entry }, CancellationToken.None, "Test.json");

        var traceFile = Path.Combine(traceRoot, "logs", "translation-trace", run.RunId + ".jsonl");
        Assert.True(File.Exists(traceFile), "Trace 文件应已生成");
        var lines = File.ReadAllLines(traceFile);
        Assert.Single(lines);
        Assert.Contains("glossaryTerms", lines[0]);

        // JSON 默认转义非 ASCII，因此解析后断言命中术语摘要
        using var doc = JsonDocument.Parse(lines[0]);
        var terms = doc.RootElement.GetProperty("glossaryTerms").GetString();
        Assert.Equal("TestTermAlpha→测试术语甲[L]", terms);
    }

    private static DeepSeekOptions MakeOptions() => new()
    {
        ApiKey = "test-key",
        ApiUrl = "https://api.deepseek.com/chat/completions",
        Model = "deepseek-v4-flash",
        MaxRetry = 0,
    };

    private static DiffEntry MakeEntry(string source, string filePath = "Test.json")
        => new()
        {
            Key = new UnitKey { RelativeFilePath = filePath, RecordId = "1", FieldPath = "dataList[0].content" },
            NewSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };

    /// <summary>记录本次调用参数的测试替身（验证术语 / 角色风格确实到达客户端边界）。</summary>
    private sealed class RecordingBatchClient : IDeepSeekBatchClient
    {
        public int CallCount { get; private set; }

        public string? LastGlossaryPrompt { get; private set; }

        public string? LastCharacterStylePrompt { get; private set; }

        public Task<DeepSeekBatchResult> TranslateBatchWithMetadataAsync(
            string batchId,
            IReadOnlyList<DeepSeekTranslateRequestItem> items,
            CancellationToken cancellationToken,
            string glossaryPrompt,
            string characterStylePrompt)
        {
            CallCount++;
            LastGlossaryPrompt = glossaryPrompt;
            LastCharacterStylePrompt = characterStylePrompt;

            var dict = new Dictionary<string, DeepSeekTranslateItem>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                dict[item.Id] = new DeepSeekTranslateItem(item.Id, "[译]" + item.Source, false, string.Empty);
            }

            return Task.FromResult(new DeepSeekBatchResult
            {
                Items = dict,
                ResponseId = "resp-test",
                ResponseModel = "deepseek-v4-flash",
                RetryCount = 0,
                DurationMs = 1,
            });
        }
    }
}