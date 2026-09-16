using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P1轮：**CLI 正式翻译入口核验**（源码级守卫）。
///
/// 目标：确认 <c>--translate</c> 与 WPF 正式 Run 使用**同一套**四模式生产链：
///   TranslationRunBootstrap（锁定 TranslationMode）
///   → OldChineseScopeLoader（KR 权威旧中文范围）
///   → ProductionTranslationPlanBuilder（ApplyToEntries + 按最终动作过滤 + 术语单次匹配）
///   → Coordinator/Agent → Merge/ReleaseGate（同一个权威 Key 集）。
/// 只读源码，不执行 CLI。
/// </summary>
public sealed class CliEntryPointTests
{
    [Fact]
    public void CLI正式入口与WPF共用四模式生产链()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "LimbusTranslator.Cli", "Program.cs"));

        // ① 模式来自生产启动助手（Run 内锁定一次，不各写一套）
        Assert.Contains("TranslationRunBootstrap.CreateProvider(", source);
        Assert.Contains("bootstrap.TranslationMode", source);

        // ② 旧中文范围（P7）：KR 权威模式必须扩到 Current KR 文件集
        Assert.Contains("OldChineseScopeLoader.Load(", source);
        Assert.Contains("oldChineseScope.Units", source);

        // ③ 生产计划：唯一接线 + 唯一过滤 + 术语单次匹配
        Assert.Contains("ProductionTranslationPlanBuilder.Build(", source);
        Assert.Contains("plan.NeedTranslate", source);
        Assert.Contains("bootstrap.GlossarySnapshot", source);

        // ④ Merge / ReleaseGate 使用同一份权威 Key 集与权威语言
        Assert.Contains("plan.AuthoritativeDirectory", source);
        Assert.Contains("plan.AuthoritativeLanguage", source);
        Assert.Contains("plan.ExpectedOutputKeys", source);

        // ⑤ 调用方不得自己拼接线或复制动作过滤谓词
        Assert.DoesNotContain("MultilingualSnapshotCapture.ApplyToEntries(", source);
        Assert.DoesNotContain("is TranslationAction.TranslateNew", source);
        Assert.DoesNotContain("or TranslationAction.TranslateModified", source);
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