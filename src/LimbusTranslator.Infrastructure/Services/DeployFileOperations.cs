namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 部署阶段使用的文件系统操作（最小测试缝）。
///
/// 生产实现走 <see cref="SystemDeployFileOperations"/>；
/// 测试实现可以在“第 N 次替换/备份”时稳定抛异常，用于验证回滚，而不依赖权限、文件锁等偶然因素。
/// 这里刻意保持极小接口，避免演变成整个项目的大型虚拟文件系统。
/// </summary>
public interface IDeployFileOperations
{
    /// <summary>文件是否存在</summary>
    bool FileExists(string path);

    /// <summary>文件长度（字节）</summary>
    long GetFileLength(string path);

    /// <summary>确保目录存在</summary>
    void EnsureDirectory(string directoryPath);

    /// <summary>复制文件</summary>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>移动/替换文件（同卷内 move，用于临时文件原子替换）</summary>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>删除文件</summary>
    void DeleteFile(string path);
}

/// <summary>
/// 生产实现：直接调用 System.IO。
/// </summary>
public sealed class SystemDeployFileOperations : IDeployFileOperations
{
    /// <summary>共享实例</summary>
    public static SystemDeployFileOperations Instance { get; } = new();

    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public long GetFileLength(string path) => new FileInfo(path).Length;

    /// <inheritdoc />
    public void EnsureDirectory(string directoryPath) => Directory.CreateDirectory(directoryPath);

    /// <inheritdoc />
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Copy(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Move(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void DeleteFile(string path) => File.Delete(path);
}
