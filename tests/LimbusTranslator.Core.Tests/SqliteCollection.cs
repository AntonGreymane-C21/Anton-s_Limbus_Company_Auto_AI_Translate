using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// SQLite 相关测试的串行化集合（第8.86轮）。
///
/// 背景：多个测试类在 Dispose 中调用 <c>SqliteConnection.ClearAllPools()</c>，
/// 这是**进程级**操作；xUnit 默认并行执行不同测试类，会出现
/// "一个类清理连接池 → 另一个类正在使用的连接被关闭 → 随机失败"。
///
/// 处理：只把使用 SQLite 的测试类归入本集合并禁用其并行，**不关闭整个项目**的并行测试。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteCollection
{
    /// <summary>集合名称。</summary>
    public const string Name = "SQLite";
}
