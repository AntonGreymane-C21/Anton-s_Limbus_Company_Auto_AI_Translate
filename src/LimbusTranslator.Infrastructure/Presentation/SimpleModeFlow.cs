namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>
/// 简易模式当前所处的阶段（每个阶段对应界面上唯一的一张「当前步骤」卡片）。
/// </summary>
public enum SimpleStage
{
    /// <summary>还没有找到游戏目录。</summary>
    LocateGame,

    /// <summary>已找到游戏，等待分析更新。</summary>
    Analyze,

    /// <summary>分析完成，有需要 AI 翻译的文本。</summary>
    Translate,

    /// <summary>分析完成，没有需要翻译的新文本（汉化已是最新）。</summary>
    UpToDate,

    /// <summary>已生成汉化，有需要人工确认的条目。</summary>
    Review,

    /// <summary>审核做过修改，需要把修改应用到汉化文件。</summary>
    ApplyReview,

    /// <summary>汉化文件已就绪，等待部署到游戏。</summary>
    Deploy,

    /// <summary>已部署完成。</summary>
    Done,
}

/// <summary>步骤条上单个步骤的状态。</summary>
public enum SimpleStepState
{
    Pending,
    Active,
    Done,
}

/// <summary>
/// 简易模式的输入快照：只包含决定「当前该做哪一步」的事实（全部由 ViewModel 从既有状态读出）。
/// </summary>
/// <param name="GameReady">游戏目录（英文 + 现有汉化目录）是否可用</param>
/// <param name="HasAnalysis">本次会话是否已完成分析</param>
/// <param name="NeedTranslateCount">本轮需要 AI 翻译的条目数</param>
/// <param name="OutputReady">本次会话是否已生成（并核验）汉化输出</param>
/// <param name="PendingReviewCount">仍需人工确认的条目数</param>
/// <param name="ReviewSkipped">用户是否选择了「稍后再审，先去部署」</param>
/// <param name="ReviewChangedSinceOutput">生成输出后是否做过审核保存（需要重新生成输出）</param>
/// <param name="Deployed">当前输出是否已成功部署</param>
public sealed record SimpleFlowSnapshot(
    bool GameReady,
    bool HasAnalysis,
    int NeedTranslateCount,
    bool OutputReady,
    int PendingReviewCount,
    bool ReviewSkipped,
    bool ReviewChangedSinceOutput,
    bool Deployed);

/// <summary>
/// 简易模式流程（纯函数，可单测）：把既有工作流状态收敛成「定位 → 分析 → 翻译 → 部署」四步。
///
/// 设计约束：
///   - 不引入任何新的业务状态，只做「当前该显示哪张卡片」的判断；
///   - 审核与「应用审核修改」归入第 ③ 步（翻译）内部，让普通用户只面对四个步骤。
/// </summary>
public static class SimpleModeFlow
{
    /// <summary>步骤条标题（固定四步）。</summary>
    public static IReadOnlyList<string> StepTitles { get; } = new[] { "定位游戏", "分析", "翻译", "部署" };

    /// <summary>根据快照决定当前阶段。</summary>
    public static SimpleStage Resolve(SimpleFlowSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.GameReady)
        {
            return SimpleStage.LocateGame;
        }

        if (!snapshot.HasAnalysis)
        {
            return SimpleStage.Analyze;
        }

        if (!snapshot.OutputReady)
        {
            return snapshot.NeedTranslateCount > 0 ? SimpleStage.Translate : SimpleStage.UpToDate;
        }

        if (snapshot.PendingReviewCount > 0 && !snapshot.ReviewSkipped)
        {
            return SimpleStage.Review;
        }

        if (snapshot.ReviewChangedSinceOutput)
        {
            return SimpleStage.ApplyReview;
        }

        return snapshot.Deployed ? SimpleStage.Done : SimpleStage.Deploy;
    }

    /// <summary>当前阶段对应步骤条上「进行中」的步骤序号（1~4；0 表示全部完成）。</summary>
    public static int ActiveStepNumber(SimpleStage stage) => stage switch
    {
        SimpleStage.LocateGame => 1,
        SimpleStage.Analyze => 2,
        SimpleStage.Translate or SimpleStage.Review or SimpleStage.ApplyReview => 3,
        SimpleStage.Deploy => 4,
        _ => 0,
    };

    /// <summary>四个步骤各自的状态（之前 = 完成，当前 = 进行中，之后 = 未开始；全部完成时全为完成）。</summary>
    public static IReadOnlyList<SimpleStepState> StepStates(SimpleStage stage)
    {
        var active = ActiveStepNumber(stage);
        var states = new SimpleStepState[StepTitles.Count];
        for (var index = 0; index < states.Length; index++)
        {
            var number = index + 1;
            states[index] = active == 0 || number < active
                ? SimpleStepState.Done
                : number == active ? SimpleStepState.Active : SimpleStepState.Pending;
        }

        return states;
    }

    /// <summary>
    /// 审核卡片上的「一句话问题说明」：优先显示第一个校验问题（另有多个时注明数量），
    /// 否则显示 AI 给出的审核原因，最后回退为通用提示。
    /// </summary>
    public static string IssueLine(IReadOnlyList<ReviewIssueRow>? issues, string? aiReviewReason)
    {
        if (issues is { Count: > 0 })
        {
            var first = issues.FirstOrDefault(issue => issue.IsBlocking) ?? issues[0];
            var line = string.IsNullOrWhiteSpace(first.Explanation)
                ? first.Name
                : $"{first.Name}：{first.Explanation}";
            return issues.Count > 1 ? $"{line}（另有 {issues.Count - 1} 个问题）" : line;
        }

        if (!string.IsNullOrWhiteSpace(aiReviewReason))
        {
            return $"AI 提示：{aiReviewReason.Trim()}";
        }

        return "AI 对这条译文不太确定，请确认是否通顺准确。";
    }

    /// <summary>
    /// 在审核队列里寻找下一条仍需确认的条目：从 <paramref name="currentIndex"/> 之后开始，到末尾后回绕。
    /// 返回 -1 表示没有其它待确认条目。
    /// </summary>
    public static int NextPendingIndex<T>(IReadOnlyList<T> items, int currentIndex, Func<T, bool> isPending)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(isPending);

        if (items.Count == 0)
        {
            return -1;
        }

        var start = currentIndex < 0 ? 0 : currentIndex + 1;
        for (var offset = 0; offset < items.Count; offset++)
        {
            var index = (start + offset) % items.Count;
            if (index == currentIndex)
            {
                continue;
            }

            if (isPending(items[index]))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>千分位格式的数量文本（如 1,284）。</summary>
    public static string FormatCount(int count)
        => count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
}
