namespace LimbusTranslator.Infrastructure.Snapshots;

/// <summary>
/// 三语快照根解析（第9.0C.1轮，修复 9.0C 报告 R4）。
///
/// 目的：Demo / TEMP 工作区**绝不能**写生产 <c>data/cache/source_snapshots</c>，
/// 而正常生产运行时必须继续使用项目正式路径。
///
/// 判定顺序（唯一实现，WPF / CLI / 测试共用）：
///   1. 环境变量 <c>LIMBUS_SNAPSHOT_ROOT</c>（内部/测试注入用，GUI **不暴露**该设置）；
///   2. Demo 工作区：源目录所在工作区内存在 <c>README_DEMO.txt</c>（由 --gui-demo-workspace 生成）；
///   3. 源目录位于系统临时目录下（TEMP 测试工作区）⇒ 使用「包含 Localize 目录的那一级」作为根；
///   4. 否则 = 项目根（生产正式路径）。
/// </summary>
public static class SnapshotRootResolver
{
    /// <summary>内部注入用环境变量名（不进入 GUI 设置）。</summary>
    public const string OverrideEnvironmentVariable = "LIMBUS_SNAPSHOT_ROOT";

    /// <summary>Demo 工作区标记文件名。</summary>
    public const string DemoMarkerFileName = "README_DEMO.txt";

    /// <summary>解析本次运行的三语快照读写根。</summary>
    /// <param name="projectRoot">项目根（生产默认值）</param>
    /// <param name="newEnglishDirectory">本次使用的新版英文目录</param>
    public static string Resolve(string projectRoot, string? newEnglishDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var overridden = Environment.GetEnvironmentVariable(OverrideEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden!;
        }

        var workspaceRoot = TryResolveWorkspaceRoot(newEnglishDirectory);
        return string.IsNullOrWhiteSpace(workspaceRoot) ? projectRoot : workspaceRoot!;
    }

    /// <summary>是否解析为「非生产」根（用于日志提示）。</summary>
    public static bool IsIsolated(string projectRoot, string resolvedRoot)
        => !string.Equals(
            Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(resolvedRoot).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 由源目录推断 Demo / TEMP 工作区根（无则 null）：
    ///   Localize/{en,kr,jp} 布局 ⇒ 上溯找到包含 <c>Localize</c> 的目录。
    /// </summary>
    private static string? TryResolveWorkspaceRoot(string? newEnglishDirectory)
    {
        if (string.IsNullOrWhiteSpace(newEnglishDirectory) || !Directory.Exists(newEnglishDirectory))
        {
            return null;
        }

        try
        {
            var localizeRoot = Path.GetDirectoryName(Path.GetFullPath(newEnglishDirectory));
            if (string.IsNullOrWhiteSpace(localizeRoot)
                || !string.Equals(Path.GetFileName(localizeRoot), "Localize", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var workspaceRoot = Path.GetDirectoryName(localizeRoot);
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                return null;
            }

            // ① Demo 工作区标记
            if (File.Exists(Path.Combine(workspaceRoot!, DemoMarkerFileName)))
            {
                return workspaceRoot;
            }

            // ② 位于系统临时目录下的测试 / 演示工作区
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (workspaceRoot!.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
            {
                return workspaceRoot;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }
}
