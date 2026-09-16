using LimbusTranslator.Infrastructure.Services;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// Diff 日志文件写入测试。
/// </summary>
public class LogFileWriterTests
{
    [Fact]
    public void 保存日志_应生成文件并返回路径()
    {
        var logsDir = Path.Combine(Path.GetTempPath(), "LT_Logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsDir);

        var file = LogFileWriter.SaveDiffLog(logsDir, new[] { "[调试] 测试1", "[调试] 测试2" });

        Assert.True(File.Exists(file));
        Assert.StartsWith("diff_", Path.GetFileName(file));
        Assert.EndsWith(".log", file);
        var content = File.ReadAllLines(file);
        Assert.Equal(2, content.Length);
        Assert.Equal("[调试] 测试1", content[0]);

        Directory.Delete(logsDir, true);
    }

    [Fact]
    public void 保存超过10个_应只保留10个()
    {
        var logsDir = Path.Combine(Path.GetTempPath(), "LT_Logs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logsDir);

        // 连续保存 15 次
        for (var i = 0; i < 15; i++)
        {
            LogFileWriter.SaveDiffLog(logsDir, new[] { $"[调试] 第{i}次" });
        }

        var files = Directory.GetFiles(logsDir, "diff_*.log");
        Assert.Equal(10, files.Length);

        Directory.Delete(logsDir, true);
    }
}
