using System.IO;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 输出工作区卫生（第9.0C.21轮 / R6）。
///
/// 背景：<c>data/output</c> 下可能残留**非汉化产物**（例如 P6 冒烟写的 <c>real_api_smoke/…</c>）。
/// 它们不属于权威结构，因此：
///   - **不能**进入输出清单（清单只记录 Merge 真正写出的文件）；
///   - **不能**被部署（部署只按清单复制）；
///   - **不能**出现在"即将复制的文件数"里（否则用户看到虚高数字）。
///
/// 本类提供唯一的判断与清理实现（纯文件系统操作，可单测）。
/// </summary>
public static class OutputWorkspaceHygiene
{
    /// <summary>非权威产物目录（相对 output 根，忽略大小写）。</summary>
    public static readonly IReadOnlyList<string> NonAuthoritativeDirectories = new[] { "real_api_smoke" };

    /// <summary>该相对路径是否属于非权威产物（位于 <see cref="NonAuthoritativeDirectories"/> 之下）。</summary>
    public static bool IsNonAuthoritative(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        foreach (var directory in NonAuthoritativeDirectories)
        {
            if (normalized.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, directory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 删除 <paramref name="outputRoot"/> 下的非权威产物目录（默认 <c>real_api_smoke</c>）。
    /// </summary>
    /// <returns>删除的文件数（目录不存在时为 0）</returns>
    public static int Cleanup(string outputRoot, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return 0;
        }

        var removed = 0;
        foreach (var directory in NonAuthoritativeDirectories)
        {
            var path = Path.Combine(outputRoot, directory);
            if (!Directory.Exists(path))
            {
                continue;
            }

            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            try
            {
                Directory.Delete(path, recursive: true);
                removed += files.Length;
                log?.Invoke($"[调试] 输出工作区清理：已删除非权威产物目录 {directory}/（{files.Length} 个文件）");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[调试] 输出工作区清理失败（不影响输出）：{directory}/ ⇒ {ex.Message}");
            }
        }

        return removed;
    }
}
