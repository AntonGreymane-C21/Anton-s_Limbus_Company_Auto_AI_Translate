using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Diagnostics;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Translation;
using LimbusTranslator.Infrastructure.Validation;
using Microsoft.Data.Sqlite;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第8.5轮：Symbol-only / Punctuation-only 文本不调用 AI。
///
/// 覆盖：分类规则（保守）、Passthrough 行为、TM / request_cache / Trace 零写入、
/// 混合批次只发普通句子、以及「结构安全不能成为后门」。
/// </summary>
[Collection(SqliteCollection.Name)]
public class SymbolPassthroughTests : IDisposable
{
    private readonly string _root;
    private readonly TranslationMemoryOptions _memoryOptions;
    private readonly SqliteTranslationMemory _memory;
    private readonly TranslationTraceWriter _trace;
    private readonly TranslationCacheServices _cacheServices;

    public SymbolPassthroughTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "LT_SYMBOL_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _memoryOptions = new TranslationMemoryOptions { DatabasePath = Path.Combine(_root, "tm.db") };
        _memory = new SqliteTranslationMemory(_memoryOptions);
        _trace = new TranslationTraceWriter(_root, TranslationRunContext.Create());
        _cacheServices = TranslationCacheServices.Create(_memory, TranslationRunContext.Create(), _trace);
    }

    public void Dispose()
    {
        _memory.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_root, true);
        }
        catch
        {
            // 临时目录清理失败可忽略
        }
    }

    private sealed class CountingProvider : ITranslationProvider
    {
        private readonly List<DiffEntry> _seen = new();

        public int CallCount { get; private set; }

        public IReadOnlyList<DiffEntry> Seen => _seen;

        public Task<IReadOnlyDictionary<string, TranslationResult>> TranslateAsync(
            IReadOnlyList<DiffEntry> entries,
            CancellationToken cancellationToken = default,
            string? stageId = null,
            IReadOnlyDictionary<string, TranslationContext>? contexts = null)
        {
            CallCount++;
            _seen.AddRange(entries);

            var results = entries.ToDictionary(
                e => e.Key.ToString(),
                e => new TranslationResult
                {
                    Key = e.Key,
                    Translation = "[AI]" + e.NewSourceText,
                    Source = TranslationSource.AI,
                },
                StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, TranslationResult>>(results);
        }
    }

    private static DiffEntry MakeEntry(int index, string source)
        => new()
        {
            Key = new UnitKey
            {
                RelativeFilePath = "Test.json",
                RecordId = index.ToString(),
                FieldPath = $"dataList[{index}].title",
            },
            NewSourceText = source,
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
        };

    private Task<AgentExecutionResult> RunAgentAsync(CountingProvider provider, params DiffEntry[] entries)
    {
        var agent = new TranslationAgent(
            provider, new RateLimitManager(4, 4), _memory, cacheServices: _cacheServices);
        return agent.ExecuteAsync("Test.json", entries);
    }

    private int CountRows(string table)
    {
        using var conn = new SqliteConnection($"Data Source={_memoryOptions.DatabasePath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int TraceLineCount()
        => _trace.FilePath is not null && File.Exists(_trace.FilePath)
            ? File.ReadLines(_trace.FilePath).Count(l => !string.IsNullOrWhiteSpace(l))
            : 0;

    [Theory]
    [InlineData("???")]
    [InlineData("...")]
    [InlineData("……")]
    [InlineData("—")]
    [InlineData("---")]
    [InlineData("!!!")]
    [InlineData("?!")]
    [InlineData("→")]
    [InlineData("★")]
    [InlineData("◆◆◆")]
    public void 纯符号文本_判定为SymbolOnly(string text)
    {
        Assert.Equal(SourceTextKind.SymbolOnly, SourceTextClassification.Classify(text));
        Assert.True(SourceTextClassification.IsSymbolOnly(text));
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("%s")]
    [InlineData("#{var}")]
    [InlineData("[TETH]")]
    [InlineData("<br>")]
    [InlineData("<color=#fff>")]
    [InlineData("HP")]
    [InlineData("123")]
    [InlineData("E.G.O")]
    [InlineData("Hello!")]
    [InlineData("HP +1")]
    [InlineData("★ HP ★")]
    public void 含字母数字或结构标记_不判定为SymbolOnly(string text)
    {
        Assert.Equal(SourceTextKind.NaturalLanguage, SourceTextClassification.Classify(text));
        Assert.False(SourceTextClassification.IsSymbolOnly(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空文本_判定为Empty(string? text)
        => Assert.Equal(SourceTextKind.Empty, SourceTextClassification.Classify(text));

    [Fact]
    public async Task 纯符号条目_原样保留且不调用Provider_不写TM与缓存与Trace()
    {
        var entry = MakeEntry(0, "???");
        var provider = new CountingProvider();

        var result = await RunAgentAsync(provider, entry);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, provider.CallCount);
        Assert.Equal("???", entry.Translation);
        Assert.Equal(TranslationSource.Passthrough, entry.Provenance);
        Assert.Equal(TranslationMemoryMatchType.None, entry.TmMatchType);
        Assert.False(entry.NeedsReview);
        Assert.Empty(entry.ValidationIssues);
        Assert.Equal(0, CountRows("translations"));
        Assert.Equal(0, CountRows("request_cache"));
        Assert.Equal(0, TraceLineCount());
    }

    [Fact]
    public async Task 混合批次_Provider只收到普通句子()
    {
        var symbol = MakeEntry(0, "……");
        var normal = MakeEntry(1, "Real sentence to translate.");

        var provider = new CountingProvider();
        await RunAgentAsync(provider, symbol, normal);

        Assert.Equal(1, provider.CallCount);
        Assert.Single(provider.Seen);
        Assert.Equal(normal.Key.ToString(), provider.Seen[0].Key.ToString());
        Assert.Equal("……", symbol.Translation);
        Assert.Equal(TranslationSource.Passthrough, symbol.Provenance);
        Assert.Equal("[AI]Real sentence to translate.", normal.Translation);
        Assert.Equal(1, CountRows("translations"));
    }

    [Fact]
    public void Passthrough来源_不产生SameAsSource警告_但结构Error仍必须暴露()
    {
        var pipeline = new ValidationPipeline();
        var entry = new DiffEntry
        {
            Key = new UnitKey { RelativeFilePath = "Test.json", RecordId = "1", FieldPath = "dataList[1].title" },
            NewSourceText = "???",
            DiffKind = DiffKind.Added,
            Action = TranslationAction.TranslateNew,
            Translation = "???",
            Provenance = TranslationSource.Passthrough,
        };

        // 1) 正常 SymbolOnly：不因 SameAsSource 产生任何 Issue
        var report = pipeline.ValidateAndApply(entry);
        Assert.Empty(report.Issues);
        Assert.False(entry.NeedsReview);

        // 2) 若结构检查确实发现 Error（例如 Provider 宽容恢复留下的占位符问题），必须暴露
        var withStructuralError = pipeline.ValidateAndApply(
            entry,
            new[]
            {
                new ValidationIssue
                {
                    Key = entry.Key,
                    Code = ValidationIssueCodes.PlaceholderMismatch,
                    Severity = ValidationSeverity.Error,
                    Category = ValidationCategory.Placeholder,
                    Validator = "PlaceholderProtector",
                    Message = "占位符结构损坏（测试构造）",
                },
            });

        Assert.True(withStructuralError.HasError);
        Assert.True(entry.NeedsReview);
        Assert.Contains(entry.ValidationIssues, i => i.Code == ValidationIssueCodes.PlaceholderMismatch);
    }
}
