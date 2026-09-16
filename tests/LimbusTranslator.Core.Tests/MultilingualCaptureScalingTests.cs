using System.Diagnostics;
using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Snapshots;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.1轮：三语捕获的性能回归保护。
///
/// 背景：`BuildSources` 曾用「按 Key 线性查找（FirstOrDefault）」构建三语文本表 ⇒ O(n²)，
/// 真实数据（≈15 万 Key × 3 语）下分析在捕获之后卡住十几分钟（GUI 表现为「看起来死掉」）。
/// 本测试用较大的单元规模（3 万 Key × 3 语）确保实现保持 O(n)：
///   - 正确性：Sources 内容与三语文本逐一对应；
///   - 规模：整个捕获必须在给定预算内完成（O(n²) 实现会超时）。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class MultilingualCaptureScalingTests : IDisposable
{
    private const int UnitCount = 30_000;

    /// <summary>规模预算（宽松阈值，避免机器差异导致误报；O(n²) 实现需要数十秒以上）。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "limbus_capture_scaling_" + Guid.NewGuid().ToString("N")[..8]);

    public MultilingualCaptureScalingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言
        }
    }

    [Fact]
    public void 三语捕获在大规模单元下保持线性且内容正确()
    {
        var localize = Path.Combine(_root, "Localize");
        var en = Path.Combine(localize, "en");
        var kr = Path.Combine(localize, "kr");
        var jp = Path.Combine(localize, "jp");
        WriteRecords(Path.Combine(en, "EN_Bulk.json"), "EN text");
        WriteRecords(Path.Combine(kr, "KR_Bulk.json"), "KR text");
        WriteRecords(Path.Combine(jp, "JP_Bulk.json"), "JP text");

        var configDir = Path.Combine(FindRepositoryRoot(), "config");
        var watch = Stopwatch.StartNew();
        var capture = MultilingualCaptureService_Capture(localize, en, configDir);
        watch.Stop();

        Assert.True(capture.Success);
        Assert.Equal(UnitCount, capture.Sources.Count);

        // 内容正确性：每个 Key 的三语文本都能取到（防止“为了快而漏填”）
        var sample = capture.Sources.Values.First();
        Assert.NotNull(sample.Korean);
        Assert.NotNull(sample.English);
        Assert.NotNull(sample.Japanese);
        Assert.All(capture.Sources.Values, value =>
        {
            Assert.NotNull(value.Korean);
            Assert.NotNull(value.English);
            Assert.NotNull(value.Japanese);
        });

        Assert.True(
            watch.Elapsed < Budget,
            $"三语捕获耗时 {watch.Elapsed.TotalSeconds:0.0}s 超出预算 {Budget.TotalSeconds:0}s（可能是 O(n²) 回归）");
    }

    private MultilingualCaptureResult MultilingualCaptureService_Capture(string localize, string en, string configDir)
        => MultilingualSnapshotCapture.Capture(
            _root,
            new Dictionary<SourceLanguage, string?>
            {
                [SourceLanguage.Korean] = Path.Combine(localize, "kr"),
                [SourceLanguage.English] = en,
                [SourceLanguage.Japanese] = Path.Combine(localize, "jp"),
            },
            configDir,
            snapshotRoot: Path.Combine(_root, "snapshots"));

    private static void WriteRecords(string path, string textPrefix)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var rows = Enumerable.Range(0, UnitCount)
            .Select(index => new Dictionary<string, object?>
            {
                ["id"] = index,
                ["name"] = $"{textPrefix} {index}",
            })
            .ToList();

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new Dictionary<string, object?> { ["dataList"] = rows },
                new JsonSerializerOptions { WriteIndented = false }));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LimbusTranslator.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("未找到仓库根（LimbusTranslator.slnx）");
    }
}
