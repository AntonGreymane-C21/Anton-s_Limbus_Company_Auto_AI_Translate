using LimbusTranslator.Core.Models;
using LimbusTranslator.Infrastructure.Glossary;
using LimbusTranslator.Infrastructure.Snapshots;

namespace LimbusTranslator.Infrastructure.Services;

/// <summary>
/// 一次生产翻译运行的「候选 / 需要翻译」计划（第9.0B-P3轮）。
/// Candidates 的动作已经是 **ApplyToEntries 接线后**的最终动作（Canonical 说了算）。
/// </summary>
public sealed class ProductionTranslationPlan
{
    /// <summary>本次 Run 锁定的翻译模式。</summary>
    public required TranslationMode Mode { get; init; }

    /// <summary>接线后的候选条目（EN 输出结构 + KR 独有 Key；已排除 SkipDeleted）。</summary>
    public required IReadOnlyList<DiffEntry> Candidates { get; init; }

    /// <summary>接线后**仍需调用 Agent** 的条目（唯一过滤谓词见 <see cref="ProductionTranslationPlanBuilder.IsTranslationRequired"/>）。</summary>
    public required IReadOnlyList<DiffEntry> NeedTranslate { get; init; }

    /// <summary>被 ApplyToEntries 写入 / 覆盖动作的条目数（EN_ONLY 恒为 0）。</summary>
    public required int PatchedCount { get; init; }

    /// <summary>接线后把「本会翻译的条目」降级为 Inherit 并用旧中文补齐译文的数量（旧中文不被 AI 覆盖）。</summary>
    public required int InheritedKeptCount { get; init; }

    /// <summary>候选里只存在于韩文树、不存在于英文输出结构的条目数（无法写入 output，仅参与翻译 / TM）。</summary>
    public required int KoreanOnlyCount { get; init; }

    /// <summary>本次是否成功获得 Canonical 捕获（false ⇒ 未接线，退回纯英文动作语义）。</summary>
    public required bool HasCanonicalCapture { get; init; }

    /// <summary>
    /// 输出结构权威语言（第9.0B-P4轮）：EN_ONLY → en；KR_EN / KR_JP / KR_ONLY → ko。
    /// 由 <see cref="TranslationModePolicy.GetOutputAuthoritativeLanguage"/> 唯一决定。
    /// </summary>
    public required SourceLanguage AuthoritativeLanguage { get; init; }

    /// <summary>
    /// 输出模板目录（第9.0B-P4轮）：EN_ONLY = 当前英文目录；KR 三模式 = 当前韩文目录。
    /// Merge 必须使用它作为模板根，ReleaseGate 必须使用 <see cref="ExpectedOutputKeys"/>。
    /// </summary>
    public required string AuthoritativeDirectory { get; init; }

    /// <summary>是否以当前韩文为输出结构权威（KR 三模式且成功接线）。</summary>
    public required bool IsKoreanAuthoritative { get; init; }

    /// <summary>允许 / 必须出现在最终 output 的候选条目（已排除 SkipDeleted；KR 模式下即当前韩文的 Key 集）。</summary>
    public required IReadOnlyList<DiffEntry> OutputEntries { get; init; }

    /// <summary>
    /// 权威结构要求的 UnitKey 集（Merge 的 expectedKeys 与 ReleaseGate 的 ExpectedKeys **同一份**）。
    /// </summary>
    public required IReadOnlyList<string> ExpectedOutputKeys { get; init; }

    /// <summary>
    /// 第9.0B-P1轮：本次注入了 <c>MatchedTerms</c> 的条目数（0 = 本次未注入术语）。
    /// </summary>
    public int MatchedTermsInjectedCount { get; init; }

    /// <summary>候选中的 Inherit 数。</summary>
    public int InheritCount => Candidates.Count(e => e.Action == TranslationAction.Inherit);
    /// <summary>候选中的 TranslateNew 数。</summary>
    public int TranslateNewCount => Candidates.Count(e => e.Action == TranslationAction.TranslateNew);

    /// <summary>候选中的 TranslateModified 数。</summary>
    public int TranslateModifiedCount => Candidates.Count(e => e.Action == TranslationAction.TranslateModified);

    /// <summary>候选中的 TranslateMissing 数。</summary>
    public int TranslateMissingCount => Candidates.Count(e => e.Action == TranslationAction.TranslateMissing);

    /// <summary>一行式摘要（日志 / GUI 展示）。</summary>
    public string Describe() =>
        $"生产翻译计划：模式={TranslationModeCodes.ToCode(Mode)}"
        + $"｜输出结构权威={SourceLanguageHelper.ToCode(AuthoritativeLanguage)}"
        + $"｜候选 {Candidates.Count}（继承 {InheritCount}｜缺译 {TranslateMissingCount}｜新增 {TranslateNewCount}｜修改 {TranslateModifiedCount}）"
        + $"｜需翻译 {NeedTranslate.Count}"
        + $"｜output 条目 {ExpectedOutputKeys.Count}"
        + $"｜接线覆盖 {PatchedCount}"
        + (InheritedKeptCount > 0 ? $"｜接线后转继承 {InheritedKeptCount}" : string.Empty)
        + (KoreanOnlyCount > 0 ? $"｜仅韩文 Key {KoreanOnlyCount}" : string.Empty)
        + (HasCanonicalCapture ? string.Empty : "｜未接线（无 Canonical 捕获）");
}

/// <summary>
/// 生产翻译顺序的唯一事实来源（第9.0B-P3轮）。
///
/// **WPF / CLI / 测试三方调用完全相同的这一份实现**，禁止任何一方自己再写一套过滤或顺序：
///
///   候选（英文输出结构 + KR 独有 Key，排除 SkipDeleted）
///   → MultilingualSnapshotCapture.ApplyToEntries（EN_ONLY 内部直接返回；三个 KR 模式由 Canonical 韩文 Diff 决定最终动作）
///   → 按**最终动作**过滤（<see cref="IsTranslationRequired"/>）
///   → Coordinator → TranslationAgent
///
/// 同时保证「接线后降级为 Inherit」的条目用旧中文补齐译文（不被 AI 覆盖），
/// 使 Merge / output 结构不会因为「动作降级后没有译文」而缺条目。
/// </summary>
public static class ProductionTranslationPlanBuilder
{
    /// <summary>
    /// 唯一过滤谓词：只有这三个动作需要真正调用 Agent。
    /// （Inherit 走继承，SkipDeleted 不输出，其余动作不得进入 Agent。）
    /// </summary>
    public static bool IsTranslationRequired(DiffEntry? entry)
        => entry is not null
           && entry.Action is TranslationAction.TranslateNew
               or TranslationAction.TranslateModified
               or TranslationAction.TranslateMissing;

    /// <summary>
    /// 构造生产翻译计划（接线 → 过滤 → 继承物化）。这是生产链的**唯一顺序实现**。
    /// </summary>
    /// <param name="mode">本次 Run 锁定的翻译模式</param>
    /// <param name="englishDiffEntries">英文 Diff 结果的全部条目（EN_ONLY 的权威结构；KR 模式仅用于取旧中文 / 说话人 / 顺序）</param>
    /// <param name="capture">三语捕获结果（null = 未接线，退回纯英文动作语义）</param>
    /// <param name="newEnglishDirectory">
    /// 新版英文目录（用于解析权威模板目录：EN_ONLY 用它本身；KR 模式用它的同级 `kr` 目录）
    /// </param>
    /// <param name="oldChineseUnits">
    /// 第9.0B-P5轮：工作流**已解析**的旧中文单元（<c>DiffResult.OldChineseUnits</c>，<c>SourceText</c> = 旧中文文本）。
    ///
    /// KR 权威模式下旧中文一律**以 UnitKey 为准**：即使某个 Key 只存在于韩文树（英文 Diff 里没有），
    /// 只要旧中文里有同一个 UnitKey，就必须照常 Inherit（不得退化成 TranslateMissing）。
    /// EN_ONLY 不使用本参数（其 OldTranslation 由 DiffEngine 在英文结构内给出，行为保持不变）。
    /// </param>
    /// <param name="glossarySnapshot">
    /// 第9.0B-P1轮：本次 Run 锁定的术语快照。<b>术语只在计划里匹配一次</b>（按模式的多源文本），
    /// 结果写入 <c>DiffEntry.MatchedTerms</c>，供 Prompt / TerminologyValidator / Trace 共用。
    /// null = 本次不注入（旧路径仍由 Provider / Pipeline 自行匹配）。
    /// </param>
    public static ProductionTranslationPlan Build(
        TranslationMode mode,
        IReadOnlyList<DiffEntry> englishDiffEntries,
        MultilingualCaptureResult? capture,
        string newEnglishDirectory,
        IReadOnlyList<TranslationUnit>? oldChineseUnits = null,
        ActiveGlossarySnapshot? glossarySnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(englishDiffEntries);
        ArgumentException.ThrowIfNullOrWhiteSpace(newEnglishDirectory);

        // ① 英文输出结构（保留 DiffEngine 的 Inherit 译文）：
        //    EN_ONLY 的唯一候选来源；KR 模式下仅作为「旧中文 / 说话人 / 顺序」的来源
        var englishStructure = englishDiffEntries
            .Where(entry => entry.Action != TranslationAction.SkipDeleted)
            .ToList();

        // ② 输出结构权威（第9.0B-P4轮）：**唯一由 TranslationModePolicy 决定**
        //    EN_ONLY → 当前英文；KR_EN / KR_JP / KR_ONLY → 当前韩文（EN / JP 只是参考译本）
        var authoritativeLanguage = TranslationModePolicy.GetOutputAuthoritativeLanguage(mode);
        var authoritativeDirectory = newEnglishDirectory;
        var koreanAuthoritative = false;

        if (authoritativeLanguage == SourceLanguage.Korean)
        {
            var koreanDirectory = ResolveSiblingDirectory(newEnglishDirectory, SourceLanguage.Korean);
            if (capture?.CanonicalDiff is not null && koreanDirectory is not null)
            {
                authoritativeDirectory = koreanDirectory;
                koreanAuthoritative = true;
            }
            else
            {
                // 没有 Canonical 捕获 / 找不到韩文目录：不猜测，退回英文权威结构（与动作语义退化保持一致）
                authoritativeLanguage = SourceLanguage.English;
            }
        }

        var candidates = new List<DiffEntry>();
        var koreanOnly = 0;

        if (koreanAuthoritative)
        {
            // KR 模式：候选 = **当前韩文的 Key 集**
            //   英文里残留、但当前韩文已不存在的旧 Key 一律不得进入输出结构（禁止 KR ∪ EN 并集）
            var englishLookup = new Dictionary<string, DiffEntry>(StringComparer.Ordinal);
            foreach (var entry in englishStructure)
            {
                englishLookup.TryAdd(entry.Key.ToString(), entry);
            }

            // 第9.0B-P5轮：旧中文以 **UnitKey** 为准（唯一事实来源 = 工作流已解析的旧中文单元）
            //   这样「只在韩文树里存在」的 Key 也能按 Key 取到旧中文 ⇒ Inherit，而不是无意义地 TranslateMissing
            var oldChineseLookup = BuildOldChineseLookup(oldChineseUnits);

            var index = 0;
            foreach (var canonical in capture!.CanonicalDiff!.Entries)
            {
                var key = canonical.Key.ToString();
                englishLookup.TryGetValue(key, out var reference);
                if (reference is null)
                {
                    koreanOnly++;
                }

                candidates.Add(new DiffEntry
                {
                    Key = canonical.Key,
                    // 韩文 Canonical Diff 决定「源文是否变化」
                    NewSourceText = canonical.NewSourceText,
                    OldSourceText = canonical.OldSourceText,
                    DiffKind = canonical.DiffKind,
                    // 旧中文：优先英文输出结构（同 Key），否则按 UnitKey 直接取旧中文（KR-only Key 也能继承）
                    OldTranslation = reference?.OldTranslation
                        ?? (oldChineseLookup.TryGetValue(key, out var oldChinese) ? oldChinese : null),
                    Speaker = reference?.Speaker,
                    Order = reference?.Order ?? index,
                    // 占位动作：ApplyToEntries 会用 Canonical 计划覆盖（它覆盖全部韩文 Key）
                    Action = TranslationAction.Inherit,
                });
                index++;
            }
        }
        else
        {
            candidates.AddRange(englishStructure);
        }

        // ③ 旧中文可用性（唯一事实来源：候选中真的存在旧中文文本）
        var oldChineseUnitKeys = candidates
            .Where(entry => !string.IsNullOrWhiteSpace(entry.OldTranslation))
            .Select(entry => entry.Key.ToString())
            .ToList();

        // ④ 接线：动作 / 三语字段 / Mode Salt / Selected Source 全部由 ApplyToEntries 决定
        //    EN_ONLY：内部直接返回 0 ⇒ 历史英文语义逐字节不变
        var patched = capture is null
            ? 0
            : MultilingualSnapshotCapture.ApplyToEntries(candidates, capture, mode, oldChineseUnitKeys);

        // ⑤ 物化继承：接线把动作降级为 Inherit 的条目不再交给 Agent，
        //    但输出结构必须拿到译文 ⇒ 直接使用旧中文（绝不生成 AI 译文覆盖旧中文）
        var inheritedKept = 0;
        foreach (var entry in candidates)
        {
            if (entry.Action != TranslationAction.Inherit || entry.Translation is not null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.OldTranslation))
            {
                continue;
            }

            entry.Translation = entry.OldTranslation;
            entry.Provenance = TranslationSource.Inherited;
            entry.NeedsReview = false;
            entry.ReviewReason = null;
            inheritedKept++;
        }

        // ⑥ 接线后过滤：这是 Agent 唯一入口（禁止任何调用方自行复制这套谓词）
        var needTranslate = candidates.Where(IsTranslationRequired).ToList();

        // ⑦ 输出结构 Key 集（第9.0B-P4轮）：Merge 与 ReleaseGate 共用这**同一份**权威 Key
        var outputEntries = candidates
            .Where(entry => entry.Action != TranslationAction.SkipDeleted)
            .ToList();

        // ⑧ 术语匹配（第9.0B-P1轮）：**整个 Run 每个条目只匹配一次**，按模式的多源文本
        //    （EN_ONLY → EN；KR_EN → EN+KR；KR_JP → JP+KR；KR_ONLY → KR），
        //    结果写入 DiffEntry.MatchedTerms 供 Prompt / TerminologyValidator / Trace 共用。
        var matchedTermsInjected = InjectMatchedTerms(glossarySnapshot, candidates);

        return new ProductionTranslationPlan
        {
            Mode = mode,
            Candidates = candidates,
            NeedTranslate = needTranslate,
            OutputEntries = outputEntries,
            ExpectedOutputKeys = outputEntries.Select(entry => entry.Key.ToString()).ToList(),
            AuthoritativeLanguage = authoritativeLanguage,
            AuthoritativeDirectory = authoritativeDirectory,
            IsKoreanAuthoritative = koreanAuthoritative,
            PatchedCount = patched,
            InheritedKeptCount = inheritedKept,
            KoreanOnlyCount = koreanOnly,
            HasCanonicalCapture = capture is not null,
            MatchedTermsInjectedCount = matchedTermsInjected,
        };
    }

    /// <summary>
    /// 第9.0B 最终轮：解析**邻句上下文来源单元**（唯一实现，CLI / WPF / 测试共用）。
    ///
    /// 规则（依据 <see cref="TranslationModePolicy.GetNeighborSourceLanguage"/>）：
    ///   EN_ONLY / KR_EN → 英文单元（Selected Source）；
    ///   KR_JP → 日文单元；KR_ONLY → 韩文单元；
    ///   目标语言本次没有解析到单元（目录缺失 / 文件缺失）→ 回退英文单元，
    ///   保证「邻句上下文」不会因为模式差异而整个丢失。
    /// </summary>
    public static IReadOnlyList<TranslationUnit> ResolveNeighborSourceUnits(
        TranslationMode mode,
        MultilingualCaptureResult? capture,
        IReadOnlyList<TranslationUnit> newEnglishUnits)
    {
        ArgumentNullException.ThrowIfNull(newEnglishUnits);

        var language = TranslationModePolicy.GetNeighborSourceLanguage(mode);
        if (language != SourceLanguage.English
            && capture is not null
            && capture.ParsedUnits.TryGetValue(language, out var units)
            && units.Count > 0)
        {
            return units;
        }

        return newEnglishUnits;
    }

    /// <summary>
    /// 按翻译模式的多源文本匹配术语并注入条目（第9.0B-P1轮，唯一一次匹配）。
    ///
    /// 源文本来源：<see cref="DiffEntry.NewSourceText"/>（Selected：EN / JP / KR）
    /// ＋ <see cref="DiffEntry.CanonicalKoreanText"/>（韩文原文，KR 模式）。
    /// 即：EN_ONLY → EN；KR_EN → EN+KR；KR_JP → JP+KR；KR_ONLY → KR（与
    /// <see cref="TranslationModePolicy.GetGlossarySources"/> 语义一致）。
    ///
    /// 匹配实现仍是 <c>ActiveGlossarySnapshot.SelectTerms</c>（词边界 + Longest Match Wins + 跨文本并集），
    /// 本方法只负责「选择源文本」与「注入一次」。
    /// </summary>
    private static int InjectMatchedTerms(ActiveGlossarySnapshot? glossarySnapshot, IReadOnlyList<DiffEntry> candidates)
    {
        if (glossarySnapshot is null || glossarySnapshot.Count == 0)
        {
            return 0;
        }

        var injected = 0;
        foreach (var entry in candidates)
        {
            var texts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(entry.NewSourceText))
            {
                texts.Add(entry.NewSourceText!);
            }

            if (!string.IsNullOrWhiteSpace(entry.CanonicalKoreanText)
                && !texts.Contains(entry.CanonicalKoreanText!, StringComparer.Ordinal))
            {
                texts.Add(entry.CanonicalKoreanText!);
            }

            var hits = texts.Count == 0
                ? new List<KeyValuePair<string, GlossaryEntry>>()
                : glossarySnapshot.SelectTerms(texts).ToList();

            entry.MatchedTerms = hits.Count == 0
                ? Array.Empty<TerminologyRequirement>()
                : hits
                    .Select(hit => new TerminologyRequirement
                    {
                        Source = hit.Key,
                        Target = hit.Value.Translation ?? string.Empty,
                        Locked = hit.Value.Locked,
                    })
                    .ToList();

            injected++;
        }

        return injected;
    }

    /// <summary>
    /// 旧中文 UnitKey → 旧中文文本（第9.0B-P5轮）。
    /// 唯一数据来源 = 工作流已解析的旧中文单元（<see cref="DiffResult.OldChineseUnits"/>），不做第二次解析；
    /// 同一 Key 首次出现优先，空白文本不参与。
    /// </summary>
    private static Dictionary<string, string> BuildOldChineseLookup(IReadOnlyList<TranslationUnit>? oldChineseUnits)
    {
        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unit in oldChineseUnits ?? Array.Empty<TranslationUnit>())
        {
            var key = unit.Key.ToString();
            if (lookup.ContainsKey(key) || string.IsNullOrWhiteSpace(unit.SourceText))
            {
                continue;
            }

            lookup[key] = unit.SourceText;
        }

        return lookup;
    }

    /// <summary>新版英文目录的同级语言目录（不存在 → null）。</summary>
    private static string? ResolveSiblingDirectory(string newEnglishDirectory, SourceLanguage language)
    {
        var parent = Path.GetDirectoryName(newEnglishDirectory);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        var candidate = Path.Combine(parent!, SourceLanguageHelper.GetLocalizeDirectoryName(language));
        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// 三语快照捕获（WPF / CLI 共用，消除重复实现）。
    /// KR 目录取新版英文目录的同级 `kr`；JP 取同级 `jp`；失败 / 异常只记录日志并返回 null（不影响翻译主链）。
    /// </summary>
    /// <param name="projectRoot">项目根（快照落盘位置）</param>
    /// <param name="newEnglishDirectory">新版英文目录（Localize/{en}）</param>
    /// <param name="configDir">配置目录（字段规则）</param>
    /// <param name="preParsedEnglish">调用方已解析的新版英文单元（避免重复扫描）</param>
    /// <param name="log">日志回调</param>
    /// <param name="snapshotRoot">
    /// 第9.0C.1轮：三语快照读写根（默认 = <paramref name="projectRoot"/>）。
    /// Demo / TEMP 工作区通过它隔离，绝不写生产 <c>data/cache/source_snapshots</c>。
    /// </param>
    /// <param name="cancellationToken">取消令牌（取消时抛出 <see cref="OperationCanceledException"/>，不写任何快照）</param>
    public static MultilingualCaptureResult? TryCapture(
        string projectRoot,
        string newEnglishDirectory,
        string? configDir,
        IReadOnlyList<TranslationUnit>? preParsedEnglish = null,
        Action<string>? log = null,
        string? snapshotRoot = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(newEnglishDirectory);

        try
        {
            var localizeRoot = Path.GetDirectoryName(newEnglishDirectory);

            string? ResolveSibling(SourceLanguage language)
            {
                if (string.IsNullOrWhiteSpace(localizeRoot))
                {
                    return null;
                }

                var candidate = Path.Combine(localizeRoot!, SourceLanguageHelper.GetLocalizeDirectoryName(language));
                return Directory.Exists(candidate) ? candidate : null;
            }

            var capture = MultilingualSnapshotCapture.Capture(
                projectRoot,
                new Dictionary<SourceLanguage, string?>
                {
                    [SourceLanguage.Korean] = ResolveSibling(SourceLanguage.Korean),
                    [SourceLanguage.English] = newEnglishDirectory,
                    [SourceLanguage.Japanese] = ResolveSibling(SourceLanguage.Japanese),
                },
                configDir,
                log,
                preParsedEnglish: preParsedEnglish,
                snapshotRoot: snapshotRoot,
                cancellationToken: cancellationToken);

            if (!capture.Success)
            {
                log?.Invoke($"[错误] {capture.FailureReason}（Canonical Diff 未执行；快照未更新）");
                return null;
            }

            return capture;
        }
        catch (OperationCanceledException)
        {
            // 第9.0C.1轮：取消必须原样抛出（调用方据此进入「已取消」而不是「失败」）
            log?.Invoke("[调试] 三语快照 / Canonical Diff 已取消（未写任何快照）。");
            throw;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[错误] 三语快照 / Canonical Diff 执行异常（不影响翻译）：{ex.Message}");
            return null;
        }
    }
}
