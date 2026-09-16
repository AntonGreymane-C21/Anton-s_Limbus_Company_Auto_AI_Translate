namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// Diff 分析日志文件写入器。
/// 每次 Diff 分析后把日志保存为 logs/diff_yyyyMMdd_HHmmss.log，最多保留 10 个。
/// </summary>
public static class LogFileWriter
{
    /// <summary>最大保留日志数</summary>
    public const int MaxLogFiles = 10;

    /// <summary>
    /// 保存日志到文件，并清理超过上限的旧日志。
    /// </summary>
    /// <param name="logsRoot">日志根目录（logs/）</param>
    /// <param name="logLines">日志行</param>
    /// <returns>保存的文件路径</returns>
    public static string SaveDiffLog(string logsRoot, IEnumerable<string> logLines)
    {
        Directory.CreateDirectory(logsRoot);

        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        var filePath = Path.Combine(logsRoot, $"diff_{timestamp}.log");

        // 确保文件名唯一（同毫秒时追加序号）
        var seq = 1;
        while (File.Exists(filePath))
        {
            filePath = Path.Combine(logsRoot, $"diff_{timestamp}_{seq:D2}.log");
            seq++;
        }
        File.WriteAllLines(filePath, logLines, System.Text.Encoding.UTF8);

        // 清理旧日志，最多保留 MaxLogFiles 个（按创建时间保留最新的）
        var oldFiles = Directory
            .EnumerateFiles(logsRoot, "diff_*.log")
            .OrderByDescending(f => File.GetCreationTime(f))
            .Skip(MaxLogFiles)
            .ToList();
        foreach (var old in oldFiles)
        {
            try
            {
                File.Delete(old);
            }
            catch
            {
                // 忽略删除失败
            }
        }

        return filePath;
    }
}
