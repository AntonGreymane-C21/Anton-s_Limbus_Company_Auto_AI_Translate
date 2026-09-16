using LimbusTranslator.Core.Abstractions;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Text;
using LimbusTranslator.Infrastructure.Caching;
using LimbusTranslator.Infrastructure.Configuration;
using LimbusTranslator.Infrastructure.DeepSeek;
using LimbusTranslator.Infrastructure.Presentation;
using LimbusTranslator.Infrastructure.Services;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.3轮：**任务范围联动 + 文件自由勾选** 的验收测试。
///
/// 覆盖：文件摘要 / 默认全选 / 范围联动 / 选择保留 / 全选·全不选·反选（只影响可见范围）/
/// 重新分析后的选择收敛 / 统计 / 输出 Key 集 / Diff 动作不被改变 / Provider 只收到选中条目。
/// </summary>
public sealed class TaskSelectionTests
{
    private static DiffEntry Entry(string file, string recordId, TranslationAction action)
        => new()
        {
            Key = new UnitKey { RelativeFilePath = file, RecordId = recordId, FieldPath = "dataList[0].name" },
            NewSourceText = $"text-{recordId}",
            DiffKind = DiffKind.Modified,
            Action = action,
            Translation = null,
        };

    /// <summary>4 个逻辑文件：StoryData/A(3) StoryData/B(2) Items/C(5) Skills/D(1)。</summary>
    private static ProductionTranslationPlan Plan(int extraStoryFileEntries = 0)
    {
        var entries = new List<DiffEntry>();
        entries.AddRange(Enumerable.Range(1, 3).Select(i => Entry("StoryData/A.json", $"A{i}", TranslationAction.TranslateModified)));
        entries.AddRange(Enumerable.Range(1, 2).Select(i => Entry("StoryData/B.json", $"B{i}", TranslationAction.TranslateNew)));
        entries.AddRange(Enumerable.Range(1, 5).Select(i => Entry("Items/C.json", $"C{i}", TranslationAction.TranslateMissing)));
        entries.Add(Entry("Skills/D.json", "D1", TranslationAction.TranslateModified));
        if (extraStoryFileEntries > 0)
        {
            entries.AddRange(Enumerable.Range(1, extraStoryFileEntries)
                .Select(i => Entry("StoryData/E.json", $"E{i}", TranslationAction.TranslateNew)));
        }

        return new ProductionTranslationPlan
        {
            Mode = TranslationMode.KoreanEnglish,
            Candidates = entries,
            NeedTranslate = entries,
            OutputEntries = entries,
            ExpectedOutputKeys = entries.Select(entry => entry.Key.ToString()).ToList(),
            AuthoritativeLanguage = SourceLanguage.Korean,
            AuthoritativeDirectory = "kr",
            IsKoreanAuthoritative = true,
            PatchedCount = 0,
            InheritedKeptCount = 0,
            KoreanOnlyCount = 0,
            HasCanonicalCapture = true,
        };
    }

    private static FileSelectionState Selection() => new();

    private static HashSet<TextCategory> Scope(params TextCategory[] categories) => categories.ToHashSet();

    // ───────── 摘要 / 默认选择 ─────────

    [Fact]
    public void 文件摘要_按逻辑文件聚合且默认全选()
    {
        var summaries = TaskSelection.BuildSummaries(Plan());

        Assert.Equal(4, summaries.Count);
        Assert.Equal(3, summaries.Single(s => s.LogicalFile == "StoryData/A.json").NeedTranslateCount);
        Assert.Equal(2, summaries.Single(s => s.LogicalFile == "StoryData/B.json").NeedTranslateCount);
        Assert.Equal(5, summaries.Single(s => s.LogicalFile == "Items/C.json").NeedTranslateCount);
        Assert.True(summaries.All(s => s.RequiresAi));

        var result = TaskSelection.Resolve(Plan(), Scope(), Selection());

        Assert.Equal(11, result.SelectedEntries.Count);          // 默认全选 ⇒ 与旧行为一致
        Assert.Equal(4, result.SelectedFiles.Count);
        Assert.False(result.IsPartial);
    }

    // ───────── 任务范围联动（内存过滤） ─────────

    [Fact]
    public void 任务范围_只显示该范围的文件()
    {
        var itemsCategory = TextCategoryHelper.FromRelativePath("Items/C.json");
        var story = TaskSelection.Resolve(Plan(), Scope(TextCategory.StoryData), Selection());

        Assert.Equal(new[] { "StoryData/A.json", "StoryData/B.json" }, story.VisibleFiles);
        Assert.Equal(5, story.VisibleNeedTranslateUnitCount);
        Assert.DoesNotContain("Items/C.json", story.SelectedFiles);

        // 切到 Items 所在分类：StoryData 的文件不再出现
        var items = TaskSelection.Resolve(Plan(), Scope(itemsCategory), Selection());
        Assert.Contains("Items/C.json", items.VisibleFiles);
        Assert.DoesNotContain("StoryData/A.json", items.VisibleFiles);
        Assert.Contains(items.SelectedFiles, file => file == "Items/C.json");
    }

    [Fact]
    public void 任务范围_选择状态跨范围保留()
    {
        var itemsCategory = TextCategoryHelper.FromRelativePath("Items/C.json");
        var selection = Selection();
        selection.Set("StoryData/B.json", false);              // 先取消 B

        var items = TaskSelection.Resolve(Plan(), Scope(itemsCategory), selection);
        Assert.Contains("Items/C.json", items.SelectedFiles);   // Items 仍是选中状态

        // 切回 StoryData：B 仍然是未选中的
        var story = TaskSelection.Resolve(Plan(), Scope(TextCategory.StoryData), selection);
        Assert.Equal(new[] { "StoryData/A.json" }, story.SelectedFiles);
        Assert.Equal(3, story.SelectedEntries.Count);
        Assert.True(story.IsPartial);
        Assert.Equal(1, story.UnselectedFileCount);
        Assert.Equal(2, story.UnselectedUnitCount);
    }

    // ───────── 全选 / 全不选 / 反选：只影响可见范围 ─────────

    [Fact]
    public void 全选_只影响可见范围()
    {
        var selection = Selection();
        selection.SelectNone(new[] { "StoryData/A.json", "StoryData/B.json" });
        selection.SelectAll(new[] { "Items/C.json" });

        Assert.False(selection.IsSelected("StoryData/A.json"));
        Assert.True(selection.IsSelected("Items/C.json"));

        selection.SelectAll(new[] { "StoryData/A.json", "StoryData/B.json" });
        Assert.True(selection.IsSelected("StoryData/A.json"));
        Assert.True(selection.IsSelected("Items/C.json"));    // 其它范围不受影响
    }

    [Fact]
    public void 全不选与反选_只影响可见范围()
    {
        var selection = Selection();
        selection.SelectNone(new[] { "StoryData/A.json", "StoryData/B.json" });

        Assert.False(selection.IsSelected("StoryData/A.json"));
        Assert.True(selection.IsSelected("Items/C.json"));    // 隐藏范围保持

        selection.Invert(new[] { "StoryData/A.json", "StoryData/B.json" });
        Assert.True(selection.IsSelected("StoryData/A.json"));   // 反选回来
        Assert.True(selection.IsSelected("StoryData/B.json"));
        Assert.True(selection.IsSelected("Items/C.json"));       // 依旧不受影响

        var result = TaskSelection.Resolve(Plan(), Scope(TextCategory.StoryData), selection);
        Assert.Equal(2, result.SelectedFiles.Count);
    }

    // ───────── 重新分析：保留旧选择 / 新文件默认选中 / 消失文件移除 ─────────

    [Fact]
    public void 重新分析_保留已有选择且新文件默认选中()
    {
        var selection = Selection();
        selection.Reconcile(TaskSelection.BuildSummaries(Plan()).Select(s => s.LogicalFile));
        selection.Set("StoryData/B.json", false);
        selection.Set("Skills/D.json", false);

        // 第二次分析：A/B/D 仍存在，新增 StoryData/E
        selection.Reconcile(TaskSelection.BuildSummaries(Plan(extraStoryFileEntries: 2)).Select(s => s.LogicalFile));

        Assert.True(selection.IsSelected("StoryData/A.json"));
        Assert.False(selection.IsSelected("StoryData/B.json"));   // 保留
        Assert.False(selection.IsSelected("Skills/D.json"));      // 保留
        Assert.True(selection.IsSelected("StoryData/E.json"));    // 新文件默认选中

        // 消失的文件从状态中移除
        selection.Reconcile(new[] { "StoryData/A.json" });
        Assert.DoesNotContain("StoryData/B.json", selection.Snapshot.Keys);
        Assert.True(selection.IsSelected("StoryData/B.json"));    // 再现时按默认（选中）
    }

    // ───────── 统计 / 输出 Key 集 / 动作不被改变 ─────────

    [Fact]
    public void 统计与输出Key集_只包含选中文件()
    {
        var plan = Plan();
        var actionsBefore = plan.NeedTranslate.Select(entry => entry.Action).ToList();

        var selection = Selection();
        selection.Set("StoryData/B.json", false);
        selection.Set("Items/C.json", false);

        var result = TaskSelection.Resolve(plan, Scope(), selection);

        Assert.Equal(new[] { "Skills/D.json", "StoryData/A.json" }, result.SelectedFiles);
        Assert.Equal(4, result.SelectedEntries.Count);                  // A(3) + D(1)
        Assert.Equal(4, result.TotalFileCount);
        Assert.Equal(11, result.TotalNeedTranslateUnitCount);
        Assert.Equal(2, result.UnselectedFileCount);
        Assert.Equal(7, result.UnselectedUnitCount);                    // B(2) + C(5)
        Assert.True(result.IsPartial);

        // 输出 Key 集同样只含选中文件（Merge / ReleaseGate 使用同一份）
        Assert.All(result.ExpectedOutputKeys, key =>
            Assert.True(key.StartsWith("StoryData/A.json", StringComparison.Ordinal)
                        || key.StartsWith("Skills/D.json", StringComparison.Ordinal)));

        // 用户勾选**绝不改变** Diff 动作语义
        Assert.Equal(actionsBefore, plan.NeedTranslate.Select(entry => entry.Action).ToList());
    }

    [Fact]
    public void 无需AI的文件_默认不参与但输出仍保留()
    {
        var inherit = Entry("Items/Ready.json", "R1", TranslationAction.Inherit);
        var need = Entry("Items/C.json", "C1", TranslationAction.TranslateMissing);
        var candidates = new List<DiffEntry> { inherit, need };

        var plan = new ProductionTranslationPlan
        {
            Mode = TranslationMode.EnglishOnly,
            Candidates = candidates,
            NeedTranslate = new List<DiffEntry> { need },
            OutputEntries = candidates,
            ExpectedOutputKeys = candidates.Select(entry => entry.Key.ToString()).ToList(),
            AuthoritativeLanguage = SourceLanguage.English,
            AuthoritativeDirectory = "en",
            IsKoreanAuthoritative = false,
            PatchedCount = 0,
            InheritedKeptCount = 0,
            KoreanOnlyCount = 0,
            HasCanonicalCapture = true,
        };

        var summaries = TaskSelection.BuildSummaries(plan);
        Assert.False(summaries.Single(s => s.LogicalFile == "Items/Ready.json").RequiresAi);   // 默认不出现在列表

        var result = TaskSelection.Resolve(plan, Scope(), Selection());
        Assert.Single(result.SelectedEntries);
        Assert.Equal(2, result.SelectedOutputEntries.Count);          // 无需 AI 的文件仍进输出语义
        Assert.Equal(1, result.TotalFileCount);                       // 统计只算需要 AI 的文件
    }

    // ───────── GUI 守卫（第9.0C.3.1）：复选框必须单向绑定 + 点击写入；统计只读权威状态 ─────────

    [Fact]
    public void GUI守卫_文件复选框单向绑定且统计只读权威状态()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "LimbusTranslator.Wpf", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "LimbusTranslator.Wpf", "ViewModels", "MainViewModel.TaskSelection.cs"));

        // ① 文件任务表（FileTasks）：复选框单向绑定 + Click 写入
        //    （TwoWay 会被 DataGrid 编辑模式与行虚拟化回写污染：
        //     实测症状 = 默认全选被洗成 0 个、第一次点击只选中行不勾框）
        var gridStart = xaml.IndexOf("ItemsSource=\"{Binding FileTasks}\"", StringComparison.Ordinal);
        Assert.True(gridStart > 0, "未找到文件任务 DataGrid");
        var gridEnd = xaml.IndexOf("</DataGrid>", gridStart, StringComparison.Ordinal);
        var fileGrid = xaml[gridStart..gridEnd];

        Assert.Contains("IsChecked=\"{Binding IsSelected, Mode=OneWay}\"", fileGrid);
        Assert.Contains("Click=\"FileTaskCheckBox_Click\"", fileGrid);
        Assert.DoesNotContain("Mode=TwoWay", fileGrid);

        // ② 模板列必须只读，否则第一次点击会被 DataGrid 用于"进入编辑模式"
        Assert.Contains("DataGridTemplateColumn Header=\"本轮处理\" Width=\"80\" IsReadOnly=\"True\"", fileGrid);

        // ③ 统计 / 按钮可用性必须读权威状态，不能读 WPF 会写入的行属性
        Assert.Contains("_fileSelection.IsSelected(row.LogicalFile)", viewModel);
        Assert.DoesNotContain("FileTasks.Where(row => row.IsSelected)", viewModel);
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

/// <summary>
/// 第9.0C.3轮：**任务选择 → 真实生产链** 的端到端验收（Fake 批量客户端，0 真实网络请求）。
///
/// 证明 GUI 使用的同一个入口（<c>TaskSelection.Resolve</c> 的输出直接交给 Coordinator）：
///   勾选 ⇒ Provider 收到条目；未勾选 ⇒ Provider 调用次数为 0。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class TaskSelectionProviderE2ETests : IDisposable
{
    private readonly FourModeAgentE2EHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static Func<TranslationCacheServices, ITranslationProvider> Factory(FakeE2EBatchClient client)
        => services => new DeepSeekTranslationProvider(
            new DeepSeekOptions
            {
                ApiKey = "sk-e2e-fake",
                ApiUrl = "https://api.deepseek.com/chat/completions",
                Model = "e2e-test-model",
                MaxRetry = 0,
            },
            configDir: null,
            batchOptions: null,
            cacheServices: services,
            client: client,
            translationMode: TranslationMode.EnglishOnly);

    [Fact]
    public async Task 任务选择_勾选才调用Provider()
    {
        _harness.WriteAll(new E2ESources(new[] { "안녕하세요" }, new[] { "Hello" }, null));
        var capture = _harness.Capture();

        // ① 先按默认（全部勾选）跑一次，取得生产计划
        var baselineClient = new FakeE2EBatchClient();
        var baseline = await _harness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            capture,
            oldChinese: null,
            Factory(baselineClient),
            oldEnglish: new[] { "Hello" },
            newEnglish: new[] { "Hello" });

        var plan = baseline.Plan;
        Assert.NotEmpty(plan.NeedTranslate);

        // ② 勾选该文件 ⇒ SelectedEntries = 全部条目 ⇒ Provider 收到
        //    （用全新的临时工作区：避免第 ① 次已写入 TM / 缓存导致 Provider 不再被调用）
        using var selectedHarness = new FourModeAgentE2EHarness();
        selectedHarness.WriteAll(new E2ESources(new[] { "안녕하세요" }, new[] { "Hello" }, null));
        var selectedCapture = selectedHarness.Capture();

        var selected = new FileSelectionState();
        var selectedResult = TaskSelection.Resolve(plan, new HashSet<TextCategory>(), selected);
        Assert.Equal(plan.NeedTranslate.Count, selectedResult.SelectedEntries.Count);

        var selectedClient = new FakeE2EBatchClient();
        await selectedHarness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            selectedCapture,
            oldChinese: null,
            Factory(selectedClient),
            oldEnglish: new[] { "Hello" },
            newEnglish: new[] { "Hello" },
            entryOverride: selectedResult.SelectedEntries);
        Assert.True(selectedClient.BatchCallCount >= 1);
        Assert.Equal(selectedResult.SelectedEntries.Count, selectedClient.TotalItemCount);

        // ③ 取消勾选该文件 ⇒ SelectedEntries 为空 ⇒ Provider **一次都不调用**
        var deselected = new FileSelectionState();
        deselected.Set(plan.NeedTranslate[0].Key.RelativeFilePath, false);
        var deselectedResult = TaskSelection.Resolve(plan, new HashSet<TextCategory>(), deselected);
        Assert.Empty(deselectedResult.SelectedEntries);

        var skippedClient = new FakeE2EBatchClient();
        await selectedHarness.RunProductionChainAsync(
            TranslationMode.EnglishOnly,
            selectedCapture,
            oldChinese: null,
            Factory(skippedClient),
            oldEnglish: new[] { "Hello" },
            newEnglish: new[] { "Hello" },
            entryOverride: deselectedResult.SelectedEntries);
        Assert.Equal(0, skippedClient.BatchCallCount);

        // ④ 动作语义不受勾选影响（用户取消勾选不是新的 TranslationAction）
        Assert.All(plan.NeedTranslate, entry => Assert.NotEqual(TranslationAction.SkipDeleted, entry.Action));
    }
}
