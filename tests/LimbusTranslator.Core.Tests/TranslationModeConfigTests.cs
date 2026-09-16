using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Configuration;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0B.1轮：translationMode 配置（含旧配置迁移与 Fail-closed）。</summary>
public sealed class TranslationModeConfigTests : IDisposable
{
    private readonly string _configDir;

    public TranslationModeConfigTests()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "limbus_mode_" + Guid.NewGuid().ToString("N")[..8]);
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
            // 忽略清理失败
        }
    }

    private void WriteSettings(string json)
        => File.WriteAllText(Path.Combine(_configDir, "appsettings.json"), json);

    [Fact]
    public void 未配置默认ENONLY()
    {
        Assert.True(AppSettingsLoader.TryLoadTranslationMode(_configDir, out var mode, out var error));
        Assert.Equal(TranslationMode.EnglishOnly, mode);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("en_only", TranslationMode.EnglishOnly)]
    [InlineData("kr_en", TranslationMode.KoreanEnglish)]
    [InlineData("kr_jp", TranslationMode.KoreanJapanese)]
    [InlineData("kr_only", TranslationMode.KoreanOnly)]
    public void 四种配置码解析正确(string code, TranslationMode expected)
    {
        WriteSettings($"{{ \"translationMode\": \"{code}\" }}");

        Assert.True(AppSettingsLoader.TryLoadTranslationMode(_configDir, out var mode, out _));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("kr_fr")]
    [InlineData("en_jp")]
    [InlineData("xxx")]
    public void 非法模式FailClosed(string code)
    {
        WriteSettings($"{{ \"translationMode\": \"{code}\" }}");

        Assert.False(AppSettingsLoader.TryLoadTranslationMode(_configDir, out _, out var error));
        Assert.Contains("Unsupported translation mode", error);
    }

    [Fact]
    public void 旧配置en必须迁移为ENONLY而不是KREN()
    {
        WriteSettings("{ \"translationSourceLanguage\": \"en\" }");

        Assert.True(AppSettingsLoader.TryLoadTranslationMode(_configDir, out var mode, out _));
        Assert.Equal(TranslationMode.EnglishOnly, mode);
    }

    [Theory]
    [InlineData("ko", TranslationMode.KoreanOnly)]
    [InlineData("ja", TranslationMode.KoreanJapanese)]
    public void 旧配置ko与ja迁移正确(string legacy, TranslationMode expected)
    {
        WriteSettings($"{{ \"translationSourceLanguage\": \"{legacy}\" }}");

        Assert.True(AppSettingsLoader.TryLoadTranslationMode(_configDir, out var mode, out _));
        Assert.Equal(expected, mode);
    }

    [Fact]
    public void 新模式配置优先于旧配置()
    {
        WriteSettings("{ \"translationMode\": \"kr_jp\", \"translationSourceLanguage\": \"en\" }");

        Assert.True(AppSettingsLoader.TryLoadTranslationMode(_configDir, out var mode, out _));
        Assert.Equal(TranslationMode.KoreanJapanese, mode);
    }

    [Fact]
    public void 配置码往返稳定()
    {
        foreach (var mode in TranslationModeCodes.All)
        {
            Assert.Equal(mode, TranslationModeCodes.TryParseCode(TranslationModeCodes.ToCode(mode)));
        }
    }
}