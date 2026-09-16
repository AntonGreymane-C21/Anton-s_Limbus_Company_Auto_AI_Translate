using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0B轮：translationSourceLanguage Fail-closed + TM SourceHash 盐（语言隔离）。</summary>
[Collection(SqliteCollection.Name)]
public sealed class LanguageIsolationTests : IDisposable
{
    private readonly string _root;
    private readonly string _configDir;

    public LanguageIsolationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_iso_" + Guid.NewGuid().ToString("N")[..8]);
        _configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(_configDir);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
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

    // ── 配置 Fail-closed ────────────────────────────────────────────────

    [Fact]
    public void 未配置时_默认英文()
    {
        Assert.True(AppSettingsLoader.TryLoadTranslationSourceLanguage(_configDir, out var language, out var error));
        Assert.Equal(SourceLanguage.English, language);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("en", SourceLanguage.English)]
    [InlineData("ja", SourceLanguage.Japanese)]
    [InlineData("ko", SourceLanguage.Korean)]
    public void 合法配置_解析正确(string code, SourceLanguage expected)
    {
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), $"{{ \"translationSourceLanguage\": \"{code}\" }}");

        Assert.True(AppSettingsLoader.TryLoadTranslationSourceLanguage(_configDir, out var language, out _));
        Assert.Equal(expected, language);
    }

    [Fact]
    public void 非法配置_FailClosed且不静默变英文()
    {
        File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), "{ \"translationSourceLanguage\": \"xx\" }");

        Assert.False(AppSettingsLoader.TryLoadTranslationSourceLanguage(_configDir, out _, out var error));
        Assert.Contains("Unsupported source language: xx", error);
    }

    // ── TM SourceHash 盐 ────────────────────────────────────────────────

    [Fact]
    public void 无盐哈希_与历史EN行为完全一致()
    {
        const string text = "Hello world";
        Assert.Equal(
            SqliteTranslationMemory.ComputeSourceHash(text),
            SqliteTranslationMemory.ComputeSourceHash(text, null));
    }

    [Fact]
    public void 三种模式的盐_两两不同()
    {
        var en = SqliteTranslationMemory.BuildLanguageSalt(SourceLanguage.English, SourceLanguage.English, "가");
        var ja = SqliteTranslationMemory.BuildLanguageSalt(SourceLanguage.Japanese, SourceLanguage.Japanese, "가");
        var ko = SqliteTranslationMemory.BuildLanguageSalt(SourceLanguage.Korean, SourceLanguage.Korean, "가");

        Assert.Null(en);                       // 纯 EN → 不加盐（旧 TM 兼容）
        Assert.NotNull(ja);
        Assert.NotNull(ko);
        Assert.NotEqual(ja, ko);

        // 文本相同也必须不同哈希
        Assert.NotEqual(
            SqliteTranslationMemory.ComputeSourceHash("가", ja),
            SqliteTranslationMemory.ComputeSourceHash("가", ko));
    }

    [Fact]
    public void EN回退韩文_与JP回退韩文_盐必须不同()
    {
        var enFallback = SqliteTranslationMemory.BuildLanguageSalt(SourceLanguage.English, SourceLanguage.Korean, "가");
        var jaFallback = SqliteTranslationMemory.BuildLanguageSalt(SourceLanguage.Japanese, SourceLanguage.Korean, "가");

        Assert.NotEqual(enFallback, jaFallback);
        Assert.NotEqual(
            SqliteTranslationMemory.ComputeSourceHash("가", enFallback),
            SqliteTranslationMemory.ComputeSourceHash("가", jaFallback));
    }

    [Fact]
    public void TM层_不同语言盐的记录不得互相命中()
    {
        var options = new TranslationMemoryOptions { DatabasePath = Path.Combine(_root, "tm.db") };
        using var memory = new SqliteTranslationMemory(options);

        var key = new UnitKey { RelativeFilePath = "Items.json", RecordId = "1", FieldPath = "dataList[0].name" };
        var jaSalt = SqliteTranslationMemory.BuildLanguageSalt(SourceLanguage.Japanese, SourceLanguage.Japanese, "가");

        memory.Save(
            new TranslationUnit
            {
                Key = key,
                SourceText = "가",
                RecordId = "1",
                FieldPath = "dataList[0].name",
                FilePath = "Items.json",
                SourceHashSalt = jaSalt,
            },
            new TranslationResult { Key = key, Translation = "日语模式译文", Source = TranslationSource.AI });

        // 同语言盐 → 命中
        var hit = memory.FindExactUnit(key, SqliteTranslationMemory.ComputeSourceHash("가", jaSalt));
        Assert.NotNull(hit);
        Assert.Equal("日语模式译文", hit!.Translation);

        // 无盐（旧 EN 路径）→ 不得命中
        Assert.Null(memory.FindExactUnit(key, SqliteTranslationMemory.ComputeSourceHash("가", null)));
        // KO 模式盐 → 不得命中
        var koSalt = SqliteTranslationMemory.BuildLanguageSalt(SourceLanguage.Korean, SourceLanguage.Korean, "가");
        Assert.Null(memory.FindExactUnit(key, SqliteTranslationMemory.ComputeSourceHash("가", koSalt)));
    }
}