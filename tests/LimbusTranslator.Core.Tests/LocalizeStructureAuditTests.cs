using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Audit;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P7轮：多语言结构差异只读审计的**分级引擎**测试（Class 0～5 + ParserUnsupported）。
/// 使用 TEMP 三语树；并断言审计引擎不会修改任何文件。
/// </summary>
public sealed class LocalizeStructureAuditTests : IDisposable
{
    private readonly string _root;

    public LocalizeStructureAuditTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "limbus_audit_" + Guid.NewGuid().ToString("N")[..8]);
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
            // 临时目录清理失败可忽略
        }
    }

    private static string Items(params (int Id, string Name)[] records)
        => JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["dataList"] = records
                    .Select(record => new Dictionary<string, object?> { ["id"] = record.Id, ["name"] = record.Name })
                    .ToList(),
            });

    private void Write(SourceLanguage language, params (string File, string Json)[] files)
    {
        var directory = Path.Combine(_root, SourceLanguageHelper.GetLocalizeDirectoryName(language));
        Directory.CreateDirectory(directory);
        foreach (var (file, json) in files)
        {
            File.WriteAllText(Path.Combine(directory, SourceLanguageHelper.GetFilePrefix(language) + file), json);
        }
    }

    /// <summary>构造覆盖 Class 0～5 的三语树。</summary>
    private void WriteAuditFixture()
    {
        // Class 0：结构 + 文本完全一致
        Write(SourceLanguage.Korean, ("Identical.json", Items((1, "SAME"))));
        Write(SourceLanguage.English, ("Identical.json", Items((1, "SAME"))));

        // Class 1：结构一致，仅文本不同
        Write(SourceLanguage.Korean, ("TextOnly.json", Items((1, "안녕"))));
        Write(SourceLanguage.English, ("TextOnly.json", Items((1, "Hello"))));

        // Class 2：UnitKey 数量不同（KR 多一条），无下标错位
        Write(SourceLanguage.Korean, ("ExtraUnit.json", Items((1, "안녕"), (2, "감사"))));
        Write(SourceLanguage.English, ("ExtraUnit.json", Items((1, "Hello"))));

        // Class 3：同一语义单元（id + 字段）存在，但数组下标不同 ⇒ FieldPath 错位
        Write(SourceLanguage.Korean, ("Shifted.json", Items((2, "감사"), (1, "안녕"))));
        Write(SourceLanguage.English, ("Shifted.json", Items((1, "Hello"), (2, "Thanks"))));

        // Class 4：JSON 根类型不同（对象 vs 数组）
        Write(SourceLanguage.Korean, ("SchemaDiff.json", "{\"dataList\":{\"nested\":1}}"));
        Write(SourceLanguage.English, ("SchemaDiff.json", "[1,2,3]"));

        // Class 5-A：EN 侧 JSON 非法
        Write(SourceLanguage.Korean, ("Broken.json", Items((1, "안녕"))));
        Write(SourceLanguage.English, ("Broken.json", "{ this is not json"));

        // Class 5-B：EN 侧无可翻译字段（0 单元）
        Write(SourceLanguage.Korean, ("NoUnit.json", Items((1, "안녕"))));
        Write(SourceLanguage.English, ("NoUnit.json", "{\"dataList\":[{\"id\":1}]}"));

        // 文件集合差异：KR-only / EN-only / JP-only
        Write(SourceLanguage.Korean, ("KrOnly.json", Items((1, "안녕"))));
        Write(SourceLanguage.English, ("EnOnly.json", Items((1, "Hello"))));
        Write(SourceLanguage.Japanese, ("JpOnly.json", Items((1, "こんにちは"))));

        // 三语共同
        Write(SourceLanguage.Japanese, ("Identical.json", Items((1, "SAME"))));
    }

    private LocalizeStructureAuditReport RunAudit()
        => LocalizeStructureAuditor.Run(new LocalizeStructureAuditOptions { LocalizeRoot = _root });

    [Fact]
    public void 审计分级_Class0与Class1_同构与仅文本差异()
    {
        WriteAuditFixture();
        var report = RunAudit();

        Assert.Equal(1, report.ClassCountsKrEn[0]);   // Identical
        Assert.Equal(1, report.ClassCountsKrEn[1]);   // TextOnly
        Assert.Contains("Identical.json", report.CommonAllFiles);
    }

    [Fact]
    public void 审计分级_Class2_额外单元不影响权威模板()
    {
        WriteAuditFixture();
        var report = RunAudit();

        Assert.Equal(1, report.ClassCountsKrEn[2]);
        Assert.Equal(1, report.AffectedUnitCountsKrEn[2]);   // KR 多出 1 条
        Assert.DoesNotContain("ExtraUnit.json", report.RootKindMismatchFiles);
    }

    [Fact]
    public void 审计分级_Class3_FieldPath错位会被识别()
    {
        WriteAuditFixture();
        var report = RunAudit();

        Assert.Equal(1, report.ClassCountsKrEn[3]);
        var shifted = Assert.Single(report.FieldPathMismatchExamples);
        Assert.Equal("Shifted.json", shifted.LogicalFile);
        Assert.Equal(2, shifted.FieldPathMismatchCount);                 // 两条都错位
        Assert.Contains(shifted.FieldPathMismatchExamples, example => example.Contains("dataList[0].name"));
        Assert.Contains(shifted.FieldPathMismatchExamples, example => example.Contains("dataList[1].name"));
        Assert.Contains(report.HighRiskExamples, example => example.LogicalFile == "Shifted.json");
    }

    [Fact]
    public void 审计分级_Class4_Schema不同会被识别()
    {
        WriteAuditFixture();
        var report = RunAudit();

        Assert.Equal(1, report.ClassCountsKrEn[4]);
        Assert.Contains("SchemaDiff.json", report.RootKindMismatchFiles);
        Assert.Contains(report.HighRiskExamples, example => example.LogicalFile == "SchemaDiff.json");
    }

    [Fact]
    public void 审计分级_Class5_ParserUnsupported不导致全局崩溃()
    {
        WriteAuditFixture();
        var report = RunAudit();

        Assert.Equal(2, report.ClassCountsKrEn[5]);                      // Broken + NoUnit
        Assert.Contains("Broken.json", report.ParserUnsupportedFiles);
        Assert.Contains("NoUnit.json", report.ParserUnsupportedFiles);
        Assert.Equal(7, report.CommonKrEnFiles.Count);                   // 7 个 KR∩EN 文件仍全部完成分级
    }

    [Fact]
    public void 审计文件集合_KR_EN_JP差异被正确统计()
    {
        WriteAuditFixture();
        var report = RunAudit();

        Assert.Equal(8, report.KrFileCount);
        Assert.Equal(8, report.EnFileCount);
        Assert.Equal(2, report.JpFileCount);
        Assert.Equal(new[] { "KrOnly.json" }, report.KrOnlyFiles);
        Assert.Equal(new[] { "EnOnly.json" }, report.EnOnlyFiles);
        Assert.Equal(new[] { "JpOnly.json" }, report.JpOnlyFiles);
        Assert.True(report.AffectedUnitKeyTotal > 0);
    }

    [Fact]
    public void 审计只读_不修改任何文件()
    {
        WriteAuditFixture();
        var before = SnapshotTree();
        _ = RunAudit();
        var after = SnapshotTree();

        Assert.Equal(before, after);
    }

    [Fact]
    public void 审计_旧中文覆盖统计可跳过且不崩溃()
    {
        WriteAuditFixture();
        var report = RunAudit();

        Assert.Equal(0, report.KrOnlyFilesWithOldChinese);
        Assert.Contains(report.Notes, note => note.Contains("旧中文树"));
    }

    private IReadOnlyList<string> SnapshotTree()
        => Directory
            .EnumerateFiles(_root, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => $"{Path.GetRelativePath(_root, path)}|{new FileInfo(path).Length}|{File.GetLastWriteTimeUtc(path):o}")
            .ToList();
}
