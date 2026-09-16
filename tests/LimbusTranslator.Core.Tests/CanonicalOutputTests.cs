using System.Text.Json;
using LimbusTranslator.Core.Models;
using LimbusTranslator.Core.Release;
using LimbusTranslator.Infrastructure.Release;
using LimbusTranslator.Infrastructure.Services;
using LimbusTranslator.Infrastructure.Translation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0B-P4轮：Canonical Output 与 ReleaseGate 对齐验收。
///
/// 全部走**真实链路**：三语捕获 → ProductionTranslationPlanBuilder（权威结构）→ Agent（Fake Provider）
/// → **真实 MergeOutputService**（模板 = 权威结构）→ **真实 ReleaseGateService**（与 Merge 同一 Key 集）
/// → 解析真实 output JSON 断言 Key 是否存在。
/// 一切落盘都在临时目录；真实 DeepSeek API 调用数恒为 0；不 Deploy。
/// </summary>
[Collection(SqliteCollection.Name)]
public sealed class CanonicalOutputTests : IDisposable
{
    private const string KoreanA = "안녕하세요";
    private const string KoreanB = "반갑습니다";
    private const string EnglishC = "Hello";
    private const string EnglishOldResidual = "OLD English residual";
    private const string JapaneseC = "こんにちは";

    /// <summary>Fake Provider 的固定译文（便于断言 output 内容）。</summary>
    private const string FakeTranslation = "测试译文";

    private readonly FourModeAgentE2EHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    /// <summary>一次 Canonical Output 运行的完整观测结果。</summary>
    private sealed record CanonicalOutputRun(
        ProductionTranslationPlan Plan,
        FakeE2EProvider Provider,
        OutputMergeResult Merge,
        ReleaseGateResult Gate,
        string OutputRoot);

    /// <summary>跑一遍真实链路（capture → 计划 → Agent → Merge → Gate）。</summary>
    private async Task<CanonicalOutputRun> RunAsync(
        TranslationMode mode,
        E2ESources baseline,
        E2ESources current,
        IReadOnlyDictionary<string, string>? oldChinese,
        IReadOnlyList<string>? dropTranslationKeys = null)
    {
        var capture = _harness.CaptureAfterBaseline(baseline, current);
        var provider = new FakeE2EProvider { Mode = mode, TranslationFactory = _ => FakeTranslation };
        var run = await _harness.RunProductionChainAsync(mode, capture, oldChinese, _ => provider);
        var plan = run.Plan;

        // 真实 Merge：模板 = 权威结构（EN_ONLY → 英文目录；KR 三模式 → 韩文目录）
        var outputRoot = Path.Combine(_harness.Root, "output");
        var translations = Coordinator.CollectTranslations(plan.OutputEntries)
            .Where(pair => dropTranslationKeys is null || !dropTranslationKeys.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        var merge = new MergeOutputService().MergeAllWithReport(
            plan.AuthoritativeDirectory,
            translations,
            outputRoot,
            plan.ExpectedOutputKeys,
            plan.AuthoritativeLanguage);

        // 真实 ReleaseGate：与 Merge 完全相同的权威 Key 集
        var keySet = new ReleaseGateKeySet
        {
            ExpectedKeys = plan.ExpectedOutputKeys,
            OutputKeys = merge.WrittenKeys,
            AuthoritativeSourceCode = SourceLanguageHelper.ToCode(plan.AuthoritativeLanguage),
        };
        var gate = ReleaseGateService.Evaluate(plan.OutputEntries, null, null, keySet);

        return new CanonicalOutputRun(plan, provider, merge, gate, outputRoot);
    }

    /// <summary>解析真实 output 文件，返回 (记录 id, name 字段) 列表。</summary>
    private static List<(int Id, string? Name)> ReadOutput(CanonicalOutputRun run)
    {
        var path = Path.Combine(run.OutputRoot, FourModeAgentE2EHarness.LogicalFileName);
        Assert.True(File.Exists(path), $"output 文件不存在: {path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("dataList")
            .EnumerateArray()
            .Select(item => (item.GetProperty("id").GetInt32(), item.GetProperty("name").GetString()))
            .ToList();
    }

    private static string EnOnlyKey(int index) => FourModeAgentE2EHarness.KeyOf(index);

    // ───────── A：EN_ONLY —— KR 独有 Key 不得进入最终 output ─────────

    [Fact]
    public async Task A_ENONLY_KR独有Key不得进入output()
    {
        // 当前 EN = 3 条；当前 KR = 4 条（第 4 条只有韩文）
        var run = await RunAsync(
            TranslationMode.EnglishOnly,
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanB }, new[] { EnglishC, EnglishC, EnglishC }, null),
            oldChinese: null);

        Assert.False(run.Plan.IsKoreanAuthoritative);
        Assert.Equal(SourceLanguage.English, run.Plan.AuthoritativeLanguage);
        Assert.Equal(_harness.EnglishDirectory, run.Plan.AuthoritativeDirectory);
        Assert.Equal(3, run.Plan.ExpectedOutputKeys.Count);
        Assert.DoesNotContain(EnOnlyKey(3), run.Plan.ExpectedOutputKeys);   // KR 独有 Key 不是期望 Key
        Assert.DoesNotContain(EnOnlyKey(3), run.Merge.WrittenKeys);

        var output = ReadOutput(run);
        Assert.Equal(3, output.Count);                                      // D 没有因为 KR 存在而进入 EN_ONLY 输出
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
    }

    // ───────── B / C / D：KR_EN / KR_JP / KR_ONLY —— KR 独有 Key 必须进入 output ─────────

    [Theory]
    [InlineData(TranslationMode.KoreanEnglish)]
    [InlineData(TranslationMode.KoreanJapanese)]
    [InlineData(TranslationMode.KoreanOnly)]
    public async Task BCD_KR模式_KR独有Key必须真实进入output(TranslationMode mode)
    {
        var run = await RunAsync(
            mode,
            new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC },
                new[] { JapaneseC, JapaneseC, JapaneseC }),
            new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanB },
                new[] { EnglishC, EnglishC, EnglishC },
                new[] { JapaneseC, JapaneseC, JapaneseC }),
            oldChinese: null);

        Assert.True(run.Plan.IsKoreanAuthoritative);
        Assert.Equal(SourceLanguage.Korean, run.Plan.AuthoritativeLanguage);
        Assert.Equal(_harness.DirectoryFor(SourceLanguage.Korean), run.Plan.AuthoritativeDirectory);
        Assert.Equal(4, run.Plan.ExpectedOutputKeys.Count);
        Assert.Contains(EnOnlyKey(3), run.Plan.ExpectedOutputKeys);         // D 进入期望集
        Assert.Equal(1, run.Plan.KoreanOnlyCount);
        Assert.Equal(1, run.Provider.CallCount);                            // 只有一次 Provider 调用
        Assert.Equal(4, run.Provider.ReceivedEntries.Count);                // D 也真的被翻译了

        var output = ReadOutput(run);
        Assert.Equal(4, output.Count);                                      // D 出现在最终 output
        Assert.Equal(4, output[3].Id);
        Assert.Equal(FakeTranslation, output[3].Name);
        Assert.Contains(EnOnlyKey(3), run.Merge.WrittenKeys);
        Assert.DoesNotContain(run.Merge.Issues, issue => issue.Kind == OutputMergeIssueKind.EnglishTemplateMissing);
    }

    // ───────── E：KR 模式 —— EN 残留旧 Key 不得进入 output ─────────

    [Fact]
    public async Task E_KR模式_EN残留旧Key不得进入output()
    {
        // 当前 KR = 3 条；当前 EN = 4 条（第 4 条是英文残留的旧 Key）
        var run = await RunAsync(
            TranslationMode.KoreanEnglish,
            new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC, EnglishOldResidual },
                null),
            new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC, EnglishOldResidual },
                null),
            oldChinese: null);

        Assert.Equal(3, run.Plan.ExpectedOutputKeys.Count);
        Assert.DoesNotContain(EnOnlyKey(3), run.Plan.ExpectedOutputKeys);   // EN 旧 Key 不是期望 Key
        Assert.DoesNotContain(EnOnlyKey(3), run.Merge.WrittenKeys);

        var output = ReadOutput(run);
        Assert.Equal(3, output.Count);                                      // OLD 没有留在最终 output
        Assert.DoesNotContain(output, item => item.Name == EnglishOldResidual);
    }

    // ───────── F：KR 模式 —— Previous KR 有、Current KR 删除、EN 与旧中文仍有 ⇒ 不得进入 output ─────────

    [Fact]
    public async Task F_KR模式_韩文已删除的Key不得进入output()
    {
        var run = await RunAsync(
            TranslationMode.KoreanEnglish,
            new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA, KoreanB },
                new[] { EnglishC, EnglishC, EnglishC, EnglishC },
                null),
            new E2ESources(
                new[] { KoreanA, KoreanA, KoreanA },
                new[] { EnglishC, EnglishC, EnglishC, EnglishC },
                null),
            FourModeAgentE2EHarness.OldChinese(4));

        Assert.Equal(3, run.Plan.ExpectedOutputKeys.Count);
        Assert.DoesNotContain(EnOnlyKey(3), run.Plan.ExpectedOutputKeys);
        Assert.Equal(3, run.Plan.InheritCount);                             // 韩文未变 + 旧中文存在 ⇒ 全部继承
        Assert.Equal(3, run.Plan.InheritedKeptCount);
        Assert.Equal(0, run.Provider.CallCount);                            // 全部继承 ⇒ 不调用 Provider
        Assert.All(run.Plan.OutputEntries, entry => Assert.Equal(TranslationSource.Inherited, entry.Provenance));

        var output = ReadOutput(run);
        Assert.Equal(3, output.Count);                                      // 已删除的 D 不在 output
        Assert.DoesNotContain(output, item => item.Id == 4);
        Assert.Equal("旧中文1", output[0].Name);
    }

    // ───────── G：KR 模式 —— output 期望 Key 数 = 当前 KR Key 数 ─────────

    [Fact]
    public async Task G_KR模式_output期望Key数等于当前KR_Key数()
    {
        var korean = new[] { KoreanA, KoreanB, KoreanA, KoreanB, KoreanA };
        var english = new[] { EnglishC, EnglishC, EnglishC };   // EN 只有 3 条

        var run = await RunAsync(
            TranslationMode.KoreanOnly,
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA }, english, null),
            new E2ESources(korean, english, null),
            oldChinese: null);

        Assert.Equal(korean.Length, run.Plan.ExpectedOutputKeys.Count);
        Assert.Equal(korean.Length, run.Merge.RequestedEntryCount);
        Assert.Equal(korean.Length, run.Merge.WrittenEntryCount);
        Assert.Equal(korean.Length, ReadOutput(run).Count);
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
    }

    // ───────── H：Gate —— KR 有 X、EN 无 X、output 有 X ⇒ 不得判非预期 ─────────

    [Fact]
    public async Task H_Gate_KR独有Key不算非预期()
    {
        var run = await RunAsync(
            TranslationMode.KoreanEnglish,
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanB }, new[] { EnglishC, EnglishC, EnglishC }, null),
            oldChinese: null);

        Assert.Contains(EnOnlyKey(3), run.Merge.WrittenKeys);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.NotEqual(ReleaseGateStatus.Blocked, run.Gate.Status);
        Assert.DoesNotContain(run.Gate.Reasons, reason => reason.Kind == ReleaseGateReasonKinds.UnexpectedOutputKey);
    }

    // ───────── I：Gate —— 权威结构有 X、output 无 X ⇒ 必须 Missing 且失败 ─────────

    [Fact]
    public async Task I_Gate_缺失预期Key必须判Missing并阻断()
    {
        // 让 KR 独有 Key（第 4 条）没有译文 ⇒ Merge 整文件跳过（fail-closed）⇒ output 缺失该 Key
        var run = await RunAsync(
            TranslationMode.KoreanEnglish,
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanB }, new[] { EnglishC, EnglishC, EnglishC }, null),
            oldChinese: null,
            dropTranslationKeys: new[] { EnOnlyKey(3) });

        Assert.Empty(run.Merge.Files);                                        // 整文件不写入
        Assert.Contains(run.Merge.Issues, issue => issue.Kind == OutputMergeIssueKind.MissingTranslation);
        Assert.Equal(run.Plan.ExpectedOutputKeys.Count, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(ReleaseGateStatus.Blocked, run.Gate.Status);

        var reason = run.Gate.Reasons.Single(item => item.Kind == ReleaseGateReasonKinds.MissingExpectedKey);
        Assert.Contains(reason.Samples, sample => sample.UnitKey == EnOnlyKey(3));
        Assert.False(File.Exists(Path.Combine(run.OutputRoot, FourModeAgentE2EHarness.LogicalFileName)));
    }

    // ───────── J：Gate —— output 出现权威结构不存在的 Key ⇒ 必须判非预期 ─────────

    [Fact]
    public async Task J_Gate_输出出现权威结构不存在的Key必须判非预期()
    {
        // 真实 Merge 在 KR 权威结构下**结构上不可能**写出 EN 残留 Key（见用例 E）；
        // 这里直接验证 Gate 的守卫语义（人为把残留 Key 放进 OutputKeys）。
        var run = await RunAsync(
            TranslationMode.KoreanEnglish,
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanB }, new[] { EnglishC, EnglishC, EnglishC }, null),
            oldChinese: null);

        var residualKey = "Items.json|999|dataList[998].name";
        var keySet = new ReleaseGateKeySet
        {
            ExpectedKeys = run.Plan.ExpectedOutputKeys,
            OutputKeys = run.Plan.ExpectedOutputKeys.Concat(new[] { residualKey }).ToList(),
            AuthoritativeSourceCode = SourceLanguageHelper.ToCode(run.Plan.AuthoritativeLanguage),
        };

        var gate = ReleaseGateService.Evaluate(run.Plan.OutputEntries, null, null, keySet);

        Assert.Equal(1, gate.UnexpectedOutputKeyCount);
        Assert.Equal(0, gate.MissingExpectedKeyCount);
        Assert.Equal(ReleaseGateStatus.Blocked, gate.Status);

        var reason = gate.Reasons.Single(item => item.Kind == ReleaseGateReasonKinds.UnexpectedOutputKey);
        Assert.Contains(reason.Samples, sample => sample.UnitKey == residualKey);
    }

    // ───────── K：EN_ONLY —— Gate 保持旧语义（以英文为 Expected） ─────────

    [Fact]
    public async Task K_ENONLY_Gate以英文为Expected()
    {
        var run = await RunAsync(
            TranslationMode.EnglishOnly,
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanB }, new[] { EnglishC, EnglishC, EnglishC }, null),
            oldChinese: null);

        Assert.False(run.Plan.IsKoreanAuthoritative);
        Assert.Equal(SourceLanguage.English, run.Plan.AuthoritativeLanguage);
        Assert.Equal(3, run.Plan.ExpectedOutputKeys.Count);
        Assert.Equal(3, run.Merge.RequestedEntryCount);
        Assert.Equal(0, run.Gate.MissingExpectedKeyCount);
        Assert.Equal(0, run.Gate.UnexpectedOutputKeyCount);
        Assert.NotEqual(ReleaseGateStatus.Blocked, run.Gate.Status);
    }

    // ───────── 权威单源：输出结构权威只由翻译模式决定 ─────────

    [Theory]
    [InlineData(TranslationMode.EnglishOnly, SourceLanguage.English, false)]
    [InlineData(TranslationMode.KoreanEnglish, SourceLanguage.Korean, true)]
    [InlineData(TranslationMode.KoreanJapanese, SourceLanguage.Korean, true)]
    [InlineData(TranslationMode.KoreanOnly, SourceLanguage.Korean, true)]
    public void 输出结构权威只由翻译模式决定(TranslationMode mode, SourceLanguage expected, bool koreanOutput)
    {
        Assert.Equal(expected, TranslationModePolicy.GetOutputAuthoritativeLanguage(mode));
        Assert.Equal(koreanOutput, TranslationModePolicy.UsesCanonicalKoreanOutput(mode));
    }

    [Fact]
    public void Gate的Key语义与权威集合同源()
    {
        var expected = new[] { EnOnlyKey(0), EnOnlyKey(1) };

        var aligned = ReleaseGate.Evaluate(
            Array.Empty<DiffEntry>(),
            null,
            new ReleaseGateKeySet { ExpectedKeys = expected, OutputKeys = expected });

        Assert.Equal(0, aligned.MissingExpectedKeyCount);
        Assert.Equal(0, aligned.UnexpectedOutputKeyCount);
        Assert.Equal(ReleaseGateStatus.Passed, aligned.Status);

        var missingOnly = ReleaseGate.Evaluate(
            Array.Empty<DiffEntry>(),
            null,
            new ReleaseGateKeySet { ExpectedKeys = expected, OutputKeys = new[] { EnOnlyKey(0) } });

        Assert.Equal(1, missingOnly.MissingExpectedKeyCount);
        Assert.Equal(ReleaseGateStatus.Blocked, missingOnly.Status);
    }


    // ───────── L：P1-A 根因对照证据（旧语义：英文模板 + 英文 Key 集） ─────────

    [Fact]
    public async Task L_对照_旧英文模板下KR独有Key会被整文件拒绝()
    {
        var capture = _harness.CaptureAfterBaseline(
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA }, new[] { EnglishC, EnglishC, EnglishC }, null),
            new E2ESources(new[] { KoreanA, KoreanA, KoreanA, KoreanB }, new[] { EnglishC, EnglishC, EnglishC }, null));
        var provider = new FakeE2EProvider { Mode = TranslationMode.KoreanEnglish, TranslationFactory = _ => FakeTranslation };
        var run = await _harness.RunProductionChainAsync(
            TranslationMode.KoreanEnglish, capture, oldChinese: null, _ => provider);

        var translations = Coordinator.CollectTranslations(run.Plan.OutputEntries);
        Assert.Equal(4, translations.Count);                                   // D 已有译文（新语义下确实被翻译过）

        // 旧语义（第9.0B-P4轮之前）：模板 = 英文目录；expectedKeys = 英文结构 Key 集
        var legacyOutputRoot = Path.Combine(_harness.Root, "output-legacy");
        var legacyExpectedKeys = FourModeAgentE2EHarness
            .BuildEnglishCandidates(new[] { EnglishC, EnglishC, EnglishC }, null, new[] { EnglishC, EnglishC, EnglishC })
            .Where(entry => entry.Action != TranslationAction.SkipDeleted)
            .Select(entry => entry.Key.ToString())
            .ToList();
        Assert.Equal(3, legacyExpectedKeys.Count);

        var legacy = new MergeOutputService().MergeAllWithReport(
            _harness.EnglishDirectory,
            translations,
            legacyOutputRoot,
            legacyExpectedKeys,
            SourceLanguage.English);

        Assert.Empty(legacy.Files);                                            // 旧语义：整文件被拒绝
        Assert.Contains(legacy.Issues, issue => issue.Kind == OutputMergeIssueKind.FieldPathNotFound);
        Assert.False(File.Exists(Path.Combine(legacyOutputRoot, FourModeAgentE2EHarness.LogicalFileName)));

        // 新语义（KR 权威结构）：同一份译文可以正常写入
        var current = new MergeOutputService().MergeAllWithReport(
            run.Plan.AuthoritativeDirectory,
            translations,
            Path.Combine(_harness.Root, "output-authoritative"),
            run.Plan.ExpectedOutputKeys,
            run.Plan.AuthoritativeLanguage);

        Assert.Single(current.Files);
        Assert.Empty(current.Issues);
        Assert.Equal(4, current.WrittenEntryCount);
    }
}
