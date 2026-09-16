using Xunit;

namespace LimbusTranslator.IntegrationTests;

/// <summary>
/// SQLite 相关集成测试的串行化集合（第8.86轮）。
/// 与 Core.Tests 的集合同名但作用域限于本程序集；同样只限制 SQLite 相关测试。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqliteCollection
{
    /// <summary>集合名称。</summary>
    public const string Name = "SQLite";
}
