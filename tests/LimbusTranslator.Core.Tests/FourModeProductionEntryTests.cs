using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Persistence;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B收官轮（P0）：三语字段 / Canonical 动作 / Mode Salt **真正写入生产条目**的验证。
/// 使用真实 JSON 临时三语树 + 真实 Capture（两次以形成 previous KR）。
/// </summary>
public sealed class FourModeProductionEntryTests : IDisposable
{
    private const string Key = "Items.json|1|dataList[0].name";
    private readonly string _root;

    public FourModeProductionEntryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_final_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
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

    private static string ResolveConfigDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config");
            if (File.Exists(Path.Combine(candidate, "field_rules.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("未找到 config/field_rules.json");
    }

    private void Write(SourceLanguage language, params string[] names)
    {
        var dir = Path.Combine(_root, "Localize", SourceLanguageHelper.GetLocalizeDirectoryName(language));
        Directory.CreateDirectory(dir);
        var rows = names.Select((n, i) => $"{{\n      \"id\": {i + 1},\n      \"name\": \"{n}\"\n    }}");
        File.WriteAllText(
            Path.Combine(dir, SourceLanguageHelper.GetFilePrefix(language) + "Items.json"),
            "{\n  \"dataList\": [\n    " + string.Join(",\n    ", rows) + "\n  ]\n}\n");
    }

    private MultilingualCaptureResult Capture()
    {
        var map = new Dictionary<SourceLanguage, string?>();
        foreach (var language in SourceLanguageHelper.All)
        {
            var dir = Path.Combine(_root, "Localize", SourceLanguageHelper.GetLocalizeDirectoryName(language));
            map[language] = Directory.Exists(dir) ? dir : null;
        }

        return MultilingualSnapshotCapture.Capture(_root, map, ResolveConfigDir());
    }

    private static DiffEntry Entry(string key = Key)
    {
        var parts = key.Split('|');
        return new DiffEntry
        {
            Key = new UnitKey { RelativeFilePath = parts[0], RecordId = parts[1], FieldPath = parts[2] },
            NewSourceText = "STALE",
            OldSourceText = "OLD",
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.Inherit,
        };
    }

    /// <summary>A/B 场景：Previous KR=A / New KR=B；EN 与 JP 不变；旧中文存在。</summary>
    private (MultilingualCaptureResult Capture, DiffEntry Entry) BuildAbScenario()
    {
        Write(SourceLanguage.Korean, "안녕하세요");
        Write(SourceLanguage.English, "Hello");
        Write(SourceLanguage.Japanese, "こんにちは");
        Capture();                       // 建立 baseline（Previous KR = A）
        Write(SourceLanguage.Korean, "반갑습니다");
        return (Capture(), Entry());
    }

    [Fact]
    public void ENONLY不注入任何Canonical字段且不改动动作()
    {
        var (capture, entry) = BuildAbScenario();

        var patched = MultilingualSnapshotCapture.ApplyToEntries(new[] { entry }, capture, TranslationMode.EnglishOnly, new[] { Key });

        Assert.Equal(0, patched);
        Assert.Null(entry.CanonicalKoreanText);
        Assert.Null(entry.OldCanonicalKoreanText);
        Assert.Null(entry.SourceHashSalt);
        Assert.Equal("STALE", entry.NewSourceText);
        Assert.Equal(TranslationAction.Inherit, entry.Action);
    }

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish, "Hello")]
    [InlineData(TranslationMode.KoreanJapanese, "こんにちは")]
    [InlineData(TranslationMode.KoreanOnly, "반갑습니다")]
    public void KR模式写入三语字段与盐且动作由Canonical决定(TranslationMode mode, string expectedSelected)
    {
        var (capture, entry) = BuildAbScenario();

        var patched = MultilingualSnapshotCapture.ApplyToEntries(new[] { entry }, capture, mode, new[] { Key });

        Assert.Equal(1, patched);
        Assert.Equal("반갑습니다", entry.CanonicalKoreanText);
        Assert.Equal("안녕하세요", entry.OldCanonicalKoreanText);
        Assert.Equal(expectedSelected, entry.NewSourceText);
        Assert.NotNull(entry.SourceHashSalt);
        Assert.Equal(TranslationAction.TranslateModified, entry.Action);
    }
}