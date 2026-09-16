using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C轮：GUI 演示工作区（§61/§62）——用**真实 Diff + 生产计划**跑 TEMP 演示数据，
/// 断言 6 类条目齐全（新增 / 已修改 / 继承 / 缺少翻译 / 需要人工确认 / StoryData 邻句）。
/// 全程只写 TEMP 目录，绝不触碰生产数据或游戏目录。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class GuiDemoWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "limbus_gui_demo_test_" + Guid.NewGuid().ToString("N")[..8]);

    public GuiDemoWorkspaceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 清理失败不影响断言结果
        }
    }

    [Fact]
    public void 演示工作区_覆盖六类条目且可被真实链路解析()
    {
        var workspace = GuiDemoWorkspaceBuilder.Create(_root);

        Assert.True(File.Exists(Path.Combine(workspace.LocalizeEnglishDir, "EN_Demo.json")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Localize", "kr")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Localize", "jp")));
        Assert.True(File.Exists(Path.Combine(workspace.OldChineseDir, "Demo.json")));

        var analysis = new DiffWorkflowService(Path.Combine(FindRepositoryRoot(), "config"))
            .Analyze(workspace.OldEnglishDir, workspace.OldChineseDir, workspace.LocalizeEnglishDir, null);

        // ① 新增（英文里没有旧 Key）
        Assert.Contains(analysis.Entries, entry => entry.DiffKind == DiffKind.Added && entry.Action == TranslationAction.TranslateNew);
        // ② 已修改
        Assert.Contains(analysis.Entries, entry => entry.DiffKind == DiffKind.Modified && entry.Action == TranslationAction.TranslateModified);
        // ③ 继承（英文未变 + 旧中文存在）
        Assert.Contains(analysis.Entries, entry => entry.Action == TranslationAction.Inherit);
        // ④ 缺少翻译（英文未变 + 旧中文缺失）
        Assert.Contains(analysis.Entries, entry => entry.Action == TranslationAction.TranslateMissing);
        // ⑤ StoryData（邻句可解析的 content 行）
        Assert.Contains(analysis.Entries, entry => entry.Key.RelativeFilePath.StartsWith("StoryData/", StringComparison.Ordinal));
        // ⑥ 含韩文残留的旧中文（继承条目）：生产策略下不产生大规模 NeedsReview（只跑硬结构检查）
        var residue = analysis.Entries.FirstOrDefault(entry =>
            entry.Action == TranslationAction.Inherit && entry.Translation?.Contains('안') == true);
        Assert.NotNull(residue);
        var inheritedReport = new Infrastructure.Validation.ValidationPipeline().ValidateAndApply(residue!);
        Assert.DoesNotContain(
            inheritedReport.Issues,
            issue => issue.Code == ValidationIssueCodes.KoreanResidue);

        // 同一文本在完整策略（AI / 人工来源）下会被正确报为韩文残留 → GUI 用它演示 Validator 展示
        var fullReport = new Infrastructure.Validation.ValidationPipeline().Validate(
            new ValidationContext
            {
                Key = residue!.Key,
                SourceText = residue.NewSourceText ?? string.Empty,
                Translation = residue.Translation ?? string.Empty,
                Provenance = TranslationSource.HumanReviewed,
            },
            ValidationPolicy.Full);
        Assert.Contains(fullReport.Issues, issue => issue.Code == ValidationIssueCodes.KoreanResidue);
    }

    [Fact]
    public void 演示工作区_ENONLY与KR模式都能产出计划且不写生产数据()
    {
        var workspace = GuiDemoWorkspaceBuilder.Create(_root);
        var configDir = Path.Combine(FindRepositoryRoot(), "config");
        // 用 TEMP 作为 projectRoot ⇒ 三语快照只写在演示目录下（不碰生产 data/cache）
        var capture = ProductionTranslationPlanBuilder.TryCapture(_root, workspace.LocalizeEnglishDir, configDir);
        Assert.NotNull(capture);

        var analysis = new DiffWorkflowService(configDir)
            .Analyze(workspace.OldEnglishDir, workspace.OldChineseDir, workspace.LocalizeEnglishDir, null);

        var enOnly = ProductionTranslationPlanBuilder.Build(
            TranslationMode.EnglishOnly, analysis.Entries, capture, workspace.LocalizeEnglishDir, analysis.OldChineseUnits);
        Assert.True(enOnly.NeedTranslate.Count > 0);
        Assert.False(enOnly.IsKoreanAuthoritative);

        var krOnly = ProductionTranslationPlanBuilder.Build(
            TranslationMode.KoreanOnly, analysis.Entries, capture, workspace.LocalizeEnglishDir, analysis.OldChineseUnits);
        Assert.True(krOnly.IsKoreanAuthoritative);

        // 快照写在演示根下（TEMP），生产目录不受影响
        Assert.True(Directory.Exists(Path.Combine(_root, "data", "cache", "source_snapshots")));
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
