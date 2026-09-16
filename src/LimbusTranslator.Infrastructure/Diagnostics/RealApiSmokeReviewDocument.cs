using System.Text;
using LimbusTranslator.Core.Models;

namespace LimbusTranslator.Infrastructure.Diagnostics;

/// <summary>
/// 真实 API 冒烟「人工审阅清单」文档构造器（第8.5轮）。
///
/// 从 CLI 抽出到基础设施层的唯一目的：让"审核原因 / 结构化校验问题必须出现在清单里"
/// 这件事可以被单元测试覆盖（第8轮的人工文件只写了「需要人工审核: True」，没有原因）。
/// </summary>
public static class RealApiSmokeReviewDocument
{
    /// <summary>构造 Markdown 文本（不写文件）。</summary>
    public static string Build(
        IReadOnlyList<DiffEntry> entries,
        IReadOnlyDictionary<string, TranslationContext> contexts,
        string title = "真实 API 冒烟人工抽查",
        string roundLabel = "第8.5轮",
        IReadOnlyList<string>? summaryLines = null)
    {
        var content = new StringBuilder();
        content.AppendLine($"# {title}");
        content.AppendLine();
        content.AppendLine("本文件只供本机人工审阅，位于被 Git 忽略的 smoke 输出目录，禁止提交。");
        if (summaryLines is not null)
        {
            foreach (var line in summaryLines)
            {
                content.AppendLine(line);
            }

            content.AppendLine();
        }

        foreach (var entry in entries)
        {
            var context = contexts.TryGetValue(entry.Key.ToString(), out var found) ? found : null;
            content.AppendLine();
            content.AppendLine($"## {entry.Key}");
            content.AppendLine();
            WriteTextBlock(content, "原文", entry.NewSourceText);
            if (!string.IsNullOrWhiteSpace(entry.OldTranslation))
            {
                WriteTextBlock(content, "旧译文", entry.OldTranslation);
            }
            WriteTextBlock(content, "AI 译文", entry.Translation);
            content.AppendLine($"需要人工审核: {entry.NeedsReview}");

            // 第8.5轮：必须给出**具体**审核原因（模型自报 reason 或 Validator 结构化说明）
            content.AppendLine($"审核原因: {(string.IsNullOrWhiteSpace(entry.ReviewReason) ? "无" : entry.ReviewReason)}");

            if (entry.ValidationIssues.Count == 0)
            {
                content.AppendLine("校验问题: 无");
            }
            else
            {
                content.AppendLine("校验问题:");
                foreach (var issue in entry.ValidationIssues)
                {
                    content.AppendLine($"- {issue.Code} [{issue.Severity}]（{issue.Validator}）: {issue.Message}");
                }
            }

            content.AppendLine($"译文来源: {entry.Provenance?.ToString() ?? "未确定"}");
            if (context is not null)
            {
                content.AppendLine(
                    $"Neighbor信息摘要: Previous={(context.Previous is null ? "无" : "有")}; "
                    + $"Next={(context.Next is null ? "无" : "有")}; "
                    + $"ContextScope={(string.IsNullOrWhiteSpace(context.ContextScopeKey) ? "无" : "有")}");
            }
        }

        content.AppendLine();
        content.AppendLine("---");
        content.AppendLine("## 修改记录");
        content.AppendLine();
        content.AppendLine($"- 修改日期：{DateTime.Now:yyyy-MM-dd}");
        content.AppendLine($"- 第几轮修改：{roundLabel}");
        content.AppendLine("- 修改内容：生成真实 DeepSeek 受控冒烟的本机人工抽查清单（含审核原因与结构化校验问题）。");
        content.AppendLine("- 当前结果：仅写入独立 smoke 输出目录，未部署到游戏目录。");
        content.AppendLine("- 下一步：由用户人工抽查译文质量，不得将本文件提交 Git。");
        return content.ToString();
    }

    private static void WriteTextBlock(StringBuilder builder, string title, string? value)
    {
        builder.AppendLine($"### {title}");
        builder.AppendLine();
        builder.AppendLine("```text");
        builder.AppendLine((value ?? string.Empty).Replace("```", "` ` `", StringComparison.Ordinal));
        builder.AppendLine("```");
    }
}
