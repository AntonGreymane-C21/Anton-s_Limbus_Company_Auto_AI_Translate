using LimbusTranslator.Core.Diff;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Context;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第5轮：StoryData 邻句上下文（Context Index / Builder / 请求 / 缓存 / Trace）测试。
/// 全部使用内存数据 + 临时 SQLite + Fake Client，不访问真实 API / 真实数据库。
/// </summary>
[Collection(SqliteCollection.Name)]
public class StoryDataContextTests
{
    private const string StoryFile = "StoryData/1D101A.json";
    private const string OtherStoryFile = "StoryData/1D102A.json";

    private static TranslationUnit Unit(
        string file,
        string recordId,
        string fieldPath,
        string source,
        string? speaker = null,
        int order = 0)
    {
        var key = new UnitKey { RelativeFilePath = file, RecordId = recordId, FieldPath = fieldPath };
        return new TranslationUnit
        {
            Key = key,
            FilePath = file,
            RecordId = recordId,
            FieldPath = fieldPath,
            SourceText = source,
            Speaker = speaker,
            Order = order,
        };
    }

    /// <summary>StoryData 对话行（content 字段）</summary>
    private static TranslationUnit Line(string recordId, string source, string? speaker = null, int order = 0, string file = StoryFile)
        => Unit(file, recordId, $"dataList[{order}].content", source, speaker, order);

    private static DiffEntry Entry(TranslationUnit unit)
        => new()
        {
            Key = unit.Key,
            NewSourceText = unit.SourceText,
            OldSourceText = unit.SourceText,
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.TranslateMissing,
        };

    private static TranslationContextBuilder BuilderFor(params TranslationUnit[] units)
        => new(TranslationContextIndex.Build(units));

    private static string HashOf(TranslationContext context)
        => RequestFingerprintBuilder.Sha256Hex(DeepSeekRequestComposer.BuildContextJson(context) ?? string.Empty);

    private static CachedProviderBatchResponse ResponseWith(string translation, string id = "Test.json|1|a") => new()
    {
        Provider = "deepseek",
        Model = "test-model",
        CreatedAtUtc = DateTime.UtcNow,
        Items = new[] { new CachedProviderItem { Id = id, Translation = translation } },
    };

    private static TranslationMemoryOptions MakeOptions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "LT_CTX_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TranslationMemoryOptions { DatabasePath = Path.Combine(dir, "tm.db") };
    }

    private static void Cleanup(TranslationMemoryOptions options)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path.GetDirectoryName(options.DatabasePath)!, true);
        }
        catch
        {
            // 清理失败可忽略
        }
    }

    // ---------------- Context Index ----------------

    [Fact]
    public void 索引_基于完整新版英文_未变化文本可作Neighbor()
    {
        // A 未变化、B 需要翻译、C 未变化 —— 索引基于完整新英文，因此 A/C 都能成为 B 的邻句
        var index = TranslationContextIndex.Build(new[]
        {
            Line("1", "A spoken line.", "Dante", 0),
            Line("2", "B spoken line.", "Yi Sang", 1),
            Line("3", "C spoken line.", "Dante", 2),
        });

        Assert.Equal(3, index.NodeCount);
        Assert.Equal(1, index.ScopeCount);
        Assert.True(index.Contains($"{StoryFile}|2|dataList[1].content"));
    }

    [Fact]
    public void 索引_空文本跳过_不得成为Neighbor()
    {
        var index = TranslationContextIndex.Build(new[]
        {
            Line("1", "First line.", "Dante", 0),
            Line("2", string.Empty, "Dante", 1),
            Line("3", "   ", "Dante", 2),
            Line("4", "Last line.", "Dante", 3),
        });

        Assert.Equal(2, index.NodeCount);

        // 空文本被跳过 → 邻句直接跨越它，不会出现空 Neighbor
        var builder = new TranslationContextBuilder(index);
        var context = builder.Build(Entry(Line("4", "Last line.", "Dante", 3)));

        Assert.NotNull(context.Previous);
        Assert.Equal("First line.", context.Previous!.SourceText);
    }

    [Fact]
    public void 索引_已删除旧文本不出现()
    {
        // 索引只来自新版英文：旧版里被删除的句子（如 recordId=99）不在其中
        var index = TranslationContextIndex.Build(new[] { Line("1", "Only line.", "Dante", 0) });

        Assert.False(index.Contains($"{StoryFile}|99|dataList[9].content"));
    }

    [Fact]
    public void 索引_不跨文件()
    {
        var index = TranslationContextIndex.Build(new[]
        {
            Line("1", "File1 only line.", "Dante", 0, StoryFile),
            Line("1", "File2 only line.", "Dante", 0, OtherStoryFile),
        });

        Assert.Equal(2, index.ScopeCount);

        var builder = new TranslationContextBuilder(index);
        var context = builder.Build(Entry(Line("1", "File1 only line.", "Dante", 0, StoryFile)));

        Assert.False(context.HasNeighbors);   // 另一个文件不能“借一句”
    }

    [Fact]
    public void 索引_不跨ContextScope()
    {
        // 同一文件里两个不同的对话块：dataList（整段）与 dataList[0].talkList（嵌套段）
        var index = TranslationContextIndex.Build(new[]
        {
            Line("1", "Main block line 1.", "Dante", 0),
            Line("2", "Main block line 2.", "Dante", 1),
            Unit(StoryFile, "0", "dataList[0].talkList[0].content", "Nested block line 1.", "Dante", 0),
            Unit(StoryFile, "0", "dataList[0].talkList[1].content", "Nested block line 2.", "Dante", 0),
        });

        Assert.Equal(2, index.ScopeCount);

        var builder = new TranslationContextBuilder(index);
        var nested = builder.Build(Entry(Unit(StoryFile, "0", "dataList[0].talkList[1].content", "Nested block line 2.", "Dante", 0)));

        Assert.NotNull(nested.Previous);
        Assert.Equal("Nested block line 1.", nested.Previous!.SourceText);   // 跨段不算 Neighbor

        var mainFirst = builder.Build(Entry(Line("1", "Main block line 1.", "Dante", 0)));
        Assert.Null(mainFirst.Previous);
    }

    // ---------------- Neighbor ----------------

    [Fact]
    public void 中间句_Previous与Next正确()
    {
        var builder = BuilderFor(
            Line("1", "Line one.", "Dante", 0),
            Line("2", "Line two.", "Yi Sang", 1),
            Line("3", "Line three.", "Dante", 2));

        var context = builder.Build(Entry(Line("2", "Line two.", "Yi Sang", 1)));

        Assert.True(context.HasNeighbors);
        Assert.Equal("Line one.", context.Previous!.SourceText);
        Assert.Equal("Dante", context.Previous.Speaker);
        Assert.Equal("Line three.", context.Next!.SourceText);
        Assert.Equal(NeighborRole.Previous, context.Previous.Role);
        Assert.Equal(NeighborRole.Next, context.Next!.Role);
        Assert.Equal(0, context.Previous.SequenceIndex);
        Assert.Equal(2, context.Next.SequenceIndex);
        Assert.Equal($"{StoryFile}#dataList", context.ContextScopeKey);
    }

    [Fact]
    public void 第一句_Previous为null()
    {
        var builder = BuilderFor(
            Line("1", "Line one.", "Dante", 0),
            Line("2", "Line two.", "Dante", 1));

        var context = builder.Build(Entry(Line("1", "Line one.", "Dante", 0)));

        Assert.Null(context.Previous);
        Assert.Equal("Line two.", context.Next!.SourceText);
    }

    [Fact]
    public void 最后一句_Next为null()
    {
        var builder = BuilderFor(
            Line("1", "Line one.", "Dante", 0),
            Line("2", "Line two.", "Dante", 1));

        var context = builder.Build(Entry(Line("2", "Line two.", "Dante", 1)));

        Assert.Equal("Line one.", context.Previous!.SourceText);
        Assert.Null(context.Next);
    }

    [Fact]
    public void 单句Scope_前后都为null()
    {
        var builder = BuilderFor(Line("1", "Only line.", "Dante", 0));

        var context = builder.Build(Entry(Line("1", "Only line.", "Dante", 0)));

        Assert.False(context.HasNeighbors);
        Assert.Null(context.Previous);
        Assert.Null(context.Next);
        Assert.Equal(TranslationContext.Empty.ContextScopeKey, context.ContextScopeKey);
    }

    [Fact]
    public void 当前条目不会成为自身Neighbor()
    {
        var builder = BuilderFor(Line("1", "Only line.", "Dante", 0));

        var context = builder.Build(Entry(Line("1", "Only line.", "Dante", 0)));

        Assert.Empty(context.Neighbors);
    }


    // ---------------- Ordering ----------------

    [Fact]
    public void SequenceIndex_按Order稳定分配()
    {
        var index = TranslationContextIndex.Build(new[]
        {
            Line("2", "Second.", "Dante", 1),
            Line("1", "First.", "Dante", 0),
            Line("3", "Third.", "Dante", 2),
        });

        var builder = new TranslationContextBuilder(index);
        var second = builder.Build(Entry(Line("2", "Second.", "Dante", 1)));

        Assert.Equal(0, second.Previous!.SequenceIndex);
        Assert.Equal(2, second.Next!.SequenceIndex);
    }

    [Fact]
    public void 同Order_tie按FieldPath稳定()
    {
        // 同一记录（Order 相同）的多个可翻译字段：只有 content 参与，tie 由 FieldPath 决定
        var sameOrderUnits = new[]
        {
            Line("1", "Dialogue line.", "Dante", 0),
            Unit(StoryFile, "1", "dataList[0].content", "Dialogue line.", "Dante", 0),
        };

        var first = TranslationContextIndex.Build(sameOrderUnits);
        var second = TranslationContextIndex.Build(sameOrderUnits.Reverse().ToArray());

        Assert.Equal(first.NodeCount, second.NodeCount);
        Assert.Equal(first.ScopeCount, second.ScopeCount);
        Assert.True(first.Contains($"{StoryFile}|1|dataList[0].content"));
        Assert.True(second.Contains($"{StoryFile}|1|dataList[0].content"));
    }

    [Fact]
    public void 不同查询顺序_Context与Hash完全一致()
    {
        var units = new[]
        {
            Line("1", "Line one.", "Dante", 0),
            Line("2", "Line two.", "Yi Sang", 1),
            Line("3", "Line three.", "Dante", 2),
        };

        var builder = BuilderFor(units);
        var forward = units.Select(u => builder.Build(Entry(u))).ToArray();
        var backward = units.Reverse().Select(u => builder.Build(Entry(u))).Reverse().ToArray();

        for (var i = 0; i < units.Length; i++)
        {
            Assert.Equal(HashOf(forward[i]), HashOf(backward[i]));
            Assert.Equal(
                forward[i].Previous?.SequenceIndex,
                backward[i].Previous?.SequenceIndex);
        }
    }

    [Fact]
    public void 并发读取_Context一致()
    {
        var units = Enumerable.Range(0, 40)
            .Select(i => Line(i.ToString(), $"Line {i}.", i % 2 == 0 ? "Dante" : "Yi Sang", i))
            .ToArray();
        var builder = BuilderFor(units);
        var expected = units.Select(u => HashOf(builder.Build(Entry(u)))).ToArray();

        var results = new string[units.Length];
        Parallel.For(0, units.Length, i =>
        {
            results[i] = HashOf(builder.Build(Entry(units[i])));
        });

        Assert.Equal(expected, results);
    }

    // ---------------- 类型范围 ----------------

    [Fact]
    public void StoryData的content_启用上下文()
    {
        var builder = BuilderFor(
            Line("1", "Line one.", "Dante", 0),
            Line("2", "Line two.", "Dante", 1));

        Assert.True(builder.Build(Entry(Line("2", "Line two.", "Dante", 1))).HasNeighbors);
    }

    [Fact]
    public void 非StoryData_禁用上下文()
    {
        var builder = BuilderFor(
            Unit("Items.json", "1", "dataList[0].desc", "Item one.", null, 0),
            Unit("Items.json", "2", "dataList[1].desc", "Item two.", null, 1));

        var context = builder.Build(Entry(Unit("Items.json", "2", "dataList[1].desc", "Item two.", null, 1)));

        Assert.False(context.HasNeighbors);
    }

    [Fact]
    public void StoryData的非content字段_禁用上下文()
    {
        var builder = BuilderFor(
            Unit(StoryFile, "1", "dataList[0].title", "Grade 8 Fixer", "Yuri", 0),
            Unit(StoryFile, "2", "dataList[1].title", "Grade 7 Fixer", "Yuri", 1));

        var context = builder.Build(Entry(Unit(StoryFile, "2", "dataList[1].title", "Grade 7 Fixer", "Yuri", 1)));

        Assert.False(context.HasNeighbors);
    }


    // ---------------- 请求体 / 指纹 / 缓存 / Trace ----------------

    private sealed class FakeBatchClient : IDeepSeekBatchClient
    {
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

            return Task.FromResult(new DeepSeekBatchResult
            {
                Items = results,
                ResponseId = "resp-ctx",
                ResponseModel = "test-model",
                PromptTokens = 1,
                CompletionTokens = 2,
                TotalTokens = 3,
                RetryCount = 0,
                DurationMs = 1,
            });
        }
    }

    private static DeepSeekTranslationProvider MakeProvider(
        TranslationCacheServices? services,
        IDeepSeekBatchClient client,
        string model = "test-model")
        => new(
            new DeepSeekOptions
            {
                ApiKey = "test-key",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = model,
                MaxRetry = 0,
            },
            configDir: null,
            cacheServices: services,
            client: client);

    private static DeepSeekTranslateRequestItem RequestItem(string id, string source, TranslationContext? context)
        => new()
        {
            Id = id,
            Source = source,
            OldSource = null,
            OldTranslation = null,
            Speaker = "Dante",
            Context = context,
        };

    [Fact]
    public void Context进入UserContent_并明确只翻译Current()
    {
        var builder = BuilderFor(
            Line("1", "Line one.", "Dante", 0),
            Line("2", "Line two.", "Yi Sang", 1),
            Line("3", "Line three.", "Dante", 2));
        var context = builder.Build(Entry(Line("2", "Line two.", "Yi Sang", 1)));

        var items = new[] { RequestItem("Test.json|2|dataList[1].content", "Line two.", context) };
        var userContent = DeepSeekRequestComposer.BuildUserContent("Batch001", items);
        var systemPrompt = DeepSeekRequestComposer.BuildSystemPrompt(
            new PromptOptions(),
            string.Empty,
            string.Empty,
            DeepSeekRequestComposer.ContainsContext(items));

        Assert.Contains("上下文规则", systemPrompt);
        Assert.Contains("禁止为它们生成任何翻译项", systemPrompt);
        Assert.Contains("只能包含当前请求", systemPrompt);

        // 结构化断言：context.previous / context.next 的 speaker 与 source
        using var document = System.Text.Json.JsonDocument.Parse(userContent);
        var item = document.RootElement.GetProperty("items")[0];
        var contextNode = item.GetProperty("context");
        Assert.Equal("Dante", contextNode.GetProperty("previous").GetProperty("speaker").GetString());
        Assert.Equal("Line one.", contextNode.GetProperty("previous").GetProperty("source").GetString());
        Assert.Equal("Dante", contextNode.GetProperty("next").GetProperty("speaker").GetString());
        Assert.Equal("Line three.", contextNode.GetProperty("next").GetProperty("source").GetString());
        // 当前项自身仍以 Source 字段出现（Neighbor 不会伪装成 current）
        Assert.Equal("Line two.", item.GetProperty("Source").GetString());
    }

    [Fact]
    public void 非Story请求_UserContent不带context字段_保持字节兼容()
    {
        var items = new[] { RequestItem("Test.json|1|a", "Hello", null) };
        var userContent = DeepSeekRequestComposer.BuildUserContent("Batch001", items);

        var expected = "{\"agent_id\":\"Coordinator\",\"batch_id\":\"Batch001\",\"prompt_version\":\"1.0\","
                       + "\"items\":[{\"id\":\"" + DeepSeekResponseParser.EncodeId("Test.json|1|a")
                       + "\",\"Source\":\"Hello\",\"OldSource\":null,\"OldTranslation\":null,\"Speaker\":\"Dante\"}]}";

        Assert.Equal(expected, userContent);
        Assert.DoesNotContain("context", userContent);
        Assert.False(DeepSeekRequestComposer.ContainsContext(items));
    }


    [Fact]
    public async Task Neighbor变化_Fingerprint变化_RunId不影响()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());
            var provider = MakeProvider(services, new FakeBatchClient());

            var builder = BuilderFor(
                Line("1", "Line one.", "Dante", 0),
                Line("2", "Line two.", "Yi Sang", 1),
                Line("3", "Line three.", "Dante", 2));
            var entry = Entry(Line("2", "Line two.", "Yi Sang", 1));
            var key = entry.Key.ToString();
            var contexts = new Dictionary<string, TranslationContext> { [key] = builder.Build(entry) };

            var round1 = await provider.TranslateAsync(new[] { entry }, CancellationToken.None, StoryFile, contexts);

            // 换 RunId（新建服务实例）后，相同内容必须得到相同指纹
            var round2 = await MakeProvider(
                    TranslationCacheServices.Create(memory, TranslationRunContext.Create()),
                    new FakeBatchClient())
                .TranslateAsync(
                    new[] { Entry(Line("2", "Line two.", "Yi Sang", 1)) },
                    CancellationToken.None,
                    StoryFile,
                    contexts);

            Assert.Equal(round1[key].RequestFingerprint, round2[key].RequestFingerprint);

            // Previous 改变 → 指纹必须变化
            var changedEntry = Entry(Line("2", "Line two.", "Yi Sang", 1));
            var changedContexts = new Dictionary<string, TranslationContext>
            {
                [key] = BuilderFor(
                    Line("1", "Line one CHANGED.", "Dante", 0),
                    Line("2", "Line two.", "Yi Sang", 1),
                    Line("3", "Line three.", "Dante", 2)).Build(changedEntry),
            };
            var round3 = await MakeProvider(
                    TranslationCacheServices.Create(memory, TranslationRunContext.Create()),
                    new FakeBatchClient())
                .TranslateAsync(new[] { changedEntry }, CancellationToken.None, StoryFile, changedContexts);

            Assert.NotEqual(round1[key].RequestFingerprint, round3[key].RequestFingerprint);
        }
        finally
        {
            Cleanup(options);
        }
    }

    [Fact]
    public async Task 相同Current不同Neighbor_不命中旧缓存()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            var services = TranslationCacheServices.Create(memory, TranslationRunContext.Create());
            var client = new FakeBatchClient();
            var provider = MakeProvider(services, client);

            var entry = Entry(Line("2", "Line two.", "Yi Sang", 1));
            var key = entry.Key.ToString();

            var first = await provider.TranslateAsync(
                new[] { entry },
                CancellationToken.None,
                StoryFile,
                new Dictionary<string, TranslationContext>
                {
                    [key] = BuilderFor(
                        Line("1", "Line one.", "Dante", 0),
                        Line("2", "Line two.", "Yi Sang", 1)).Build(entry),
                });
            services.Staging!.Flush(new[] { first[key].RequestId });

            var secondEntry = Entry(Line("2", "Line two.", "Yi Sang", 1));
            var second = await provider.TranslateAsync(
                new[] { secondEntry },
                CancellationToken.None,
                StoryFile,
                new Dictionary<string, TranslationContext>
                {
                    [key] = BuilderFor(
                        Line("1", "Line one CHANGED.", "Dante", 0),
                        Line("2", "Line two.", "Yi Sang", 1)).Build(secondEntry),
                });

            Assert.Equal(2, client.CallCount);                    // 上下文变化 → 旧缓存不命中
            Assert.False(second[key].CacheHit);
        }
        finally
        {
            Cleanup(options);
        }
    }


    [Fact]
    public async Task Story请求_Trace的ContextHash稳定()
    {
        var root = Path.Combine(Path.GetTempPath(), "LT_CTXTRACE_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var memory = new SqliteTranslationMemory(
                new TranslationMemoryOptions { DatabasePath = Path.Combine(root, "tm.db") });
            var run = TranslationRunContext.Create();
            var writer = new TranslationTraceWriter(root, run);
            var services = TranslationCacheServices.Create(memory, run, writer);
            var provider = MakeProvider(services, new FakeBatchClient());

            var entry = Entry(Line("2", "Line two.", "Yi Sang", 1));
            var context = BuilderFor(
                Line("1", "Line one.", "Dante", 0),
                Line("2", "Line two.", "Yi Sang", 1),
                Line("3", "Line three.", "Dante", 2)).Build(entry);

            await provider.TranslateAsync(
                new[] { entry },
                CancellationToken.None,
                StoryFile,
                new Dictionary<string, TranslationContext> { [entry.Key.ToString()] = context });

            var lastLine = File.ReadAllLines(writer.FilePath!).Last();
            var contextHash = System.Text.Json.JsonDocument.Parse(lastLine)
                .RootElement.GetProperty("contextHash").GetString();
            Assert.False(string.IsNullOrWhiteSpace(contextHash));

            // 相同 Context（重新构建索引）→ 相同 ContextHash；不同 Context → 不同 ContextHash
            var rebuilt = BuilderFor(
                Line("1", "Line one.", "Dante", 0),
                Line("2", "Line two.", "Yi Sang", 1),
                Line("3", "Line three.", "Dante", 2)).Build(Entry(Line("2", "Line two.", "Yi Sang", 1)));
            Assert.Equal(HashOf(context), HashOf(rebuilt));

            var different = BuilderFor(
                Line("1", "Line one CHANGED.", "Dante", 0),
                Line("2", "Line two.", "Yi Sang", 1)).Build(entry);
            Assert.NotEqual(HashOf(context), HashOf(different));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // 忽略
            }
        }
    }

    // ---------------- 第4轮暂存并发补漏 ----------------

    [Fact]
    public void 并发请求_一个Flush一个Discard_互不影响()
    {
        var options = MakeOptions();
        try
        {
            using var memory = new SqliteTranslationMemory(options);
            var staging = new RequestCacheStaging(memory);

            staging.Stage("req-A", "v1:request-a", ResponseWith("译文A", "Test.json|1|a"));
            staging.Stage("req-B", "v1:request-b", ResponseWith("译文B", "Test.json|2|b"));
            Assert.Equal(2, staging.PendingCount);

            // A 先 Flush：只提交 A；B 仍处于未校验的暂存状态，不会被“搭车”提交
            var saved = staging.Flush(new[] { "req-A" });

            Assert.Equal(1, saved);
            Assert.Equal(1, staging.PendingCount);
            Assert.NotNull(memory.TryGet("v1:request-a"));
            Assert.Null(memory.TryGet("v1:request-b"));

            // B 随后判定为硬安全问题 → Discard 只丢弃自己，不影响已提交的 A
            staging.Discard("req-B");

            Assert.Equal(0, staging.PendingCount);
            Assert.NotNull(memory.TryGet("v1:request-a"));
            Assert.Null(memory.TryGet("v1:request-b"));

            // 反向顺序：先 Discard C，再 Flush D，同样互不影响
            staging.Stage("req-C", "v1:request-c", ResponseWith("译文C", "Test.json|3|c"));
            staging.Stage("req-D", "v1:request-d", ResponseWith("译文D", "Test.json|4|d"));
            staging.Discard("req-C");
            Assert.Equal(1, staging.PendingCount);

            staging.Flush(new[] { "req-D" });
            Assert.Null(memory.TryGet("v1:request-c"));
            Assert.NotNull(memory.TryGet("v1:request-d"));
        }
        finally
        {
            Cleanup(options);
        }
    }
}
