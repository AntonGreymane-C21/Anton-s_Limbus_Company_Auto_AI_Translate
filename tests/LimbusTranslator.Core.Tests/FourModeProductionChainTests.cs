using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>第9.0B-P0收口轮：Run 入口同一条 `ApplyToEntries` 路径的验收（Reference-only、Baseline 100、缺 CN、Fallback 盐）。</summary>
public sealed class FourModeProductionChainTests : IDisposable
{
    private readonly string _root;

    public FourModeProductionChainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_p0_" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string ConfigDir()
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

        return MultilingualSnapshotCapture.Capture(_root, map, ConfigDir());
    }

    private static DiffEntry Entry(int id, string? oldTranslation = "旧中文")
    {
        var key = new UnitKey { RelativeFilePath = "Items.json", RecordId = id.ToString(), FieldPath = $"dataList[{id - 1}].name" };
        return new DiffEntry
        {
            Key = key,
            NewSourceText = "STALE",
            OldSourceText = "OLD",
            OldTranslation = oldTranslation,
            DiffKind = DiffKind.Unchanged,
            Action = TranslationAction.Inherit,
        };
    }

    private static string KeyOf(int id)
        => new UnitKey { RelativeFilePath = "Items.json", RecordId = id.ToString(), FieldPath = $"dataList[{id - 1}].name" }.ToString();

    [Fact]
    public void Reference仅变化_KR未变时Action保持继承()
    {
        Write(SourceLanguage.Korean, "안녕하세요");
        Write(SourceLanguage.English, "Hello");
        Write(SourceLanguage.Japanese, "こんにちは");
        Capture();
        Write(SourceLanguage.English, "Hello changed");
        Write(SourceLanguage.Japanese, "こんばんは");
        var capture = Capture();

        Assert.Equal(0, capture.CanonicalDiff!.Modified);
        Assert.Equal(1, capture.CanonicalDiff.EnglishChangedCount);
        Assert.Equal(1, capture.CanonicalDiff.JapaneseChangedCount);

        foreach (var mode in new[] { TranslationMode.KoreanEnglish, TranslationMode.KoreanJapanese, TranslationMode.KoreanOnly })
        {
            var entry = Entry(1);
            MultilingualSnapshotCapture.ApplyToEntries(new[] { entry }, capture, mode, new[] { KeyOf(1) });
            Assert.Equal(TranslationAction.Inherit, entry.Action); // 参考译本变化不得触发重翻
        }
    }

    [Fact]
    public void 首次基线100条_全部继承且无新增或修改()
    {
        Write(SourceLanguage.Korean, Enumerable.Range(1, 100).Select(i => "원문 " + i).ToArray());
        Write(SourceLanguage.English, Enumerable.Range(1, 100).Select(i => "Source " + i).ToArray());
        var capture = Capture();

        var entries = Enumerable.Range(1, 100).Select(i => Entry(i)).ToList();
        var patched = MultilingualSnapshotCapture.ApplyToEntries(
            entries, capture, TranslationMode.KoreanEnglish, entries.Select(e => e.Key.ToString()).ToList());

        Assert.True(capture.IsFirstCanonicalBaseline);
        Assert.Equal(100, patched);
        Assert.All(entries, e => Assert.Equal(TranslationAction.Inherit, e.Action));
        Assert.DoesNotContain(entries, e => e.Action == TranslationAction.TranslateNew);
        Assert.DoesNotContain(entries, e => e.Action == TranslationAction.TranslateModified);
        Assert.All(entries, e => Assert.NotNull(e.SourceHashSalt));
    }
}