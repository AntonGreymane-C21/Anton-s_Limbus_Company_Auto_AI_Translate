namespace LimbusTranslator.Infrastructure.Presentation;

/// <summary>
/// 待审核列表的**分页**（第9.0C.12轮）—— 纯函数、无副作用，便于单测。
///
/// 背景（用户实测）：上一轮把待审核列表硬截断为前 5000 条，导致**排在第 5000 条之后的文件整批看不见**
/// （例如 StoryData/S1000B.json 等十来个文件在待审核页完全没有），而且"需要人工审核"的条目也可能被藏掉。
///
/// 现在改为：**完整集合 + 分页浏览**（每页 5000 条，可翻页 / 跳页 / 跳到文件），
/// 筛选与搜索一律作用在**完整集合**上，再对结果分页 —— 不再"先截断再筛选"。
/// </summary>
public static class ReviewPaging
{
    /// <summary>每页条目数（用户要求保持 5000）。</summary>
    public const int DefaultPageSize = 5000;

    /// <summary>总页数（至少 1 页，即使没有条目也返回 1）。</summary>
    public static int PageCount(int totalItems, int pageSize)
    {
        if (totalItems <= 0 || pageSize <= 0)
        {
            return 1;
        }

        return (int)Math.Ceiling(totalItems / (double)pageSize);
    }

    /// <summary>把页码收敛到合法范围（1..PageCount）。</summary>
    public static int ClampPage(int pageIndex, int totalItems, int pageSize)
        => Math.Clamp(pageIndex, 1, PageCount(totalItems, pageSize));

    /// <summary>取某页的条目（越界返回空）。</summary>
    public static IReadOnlyList<T> GetPage<T>(IReadOnlyList<T> all, int pageIndex, int pageSize)
    {
        if (all is null || all.Count == 0 || pageSize <= 0)
        {
            return Array.Empty<T>();
        }

        var page = ClampPage(pageIndex, all.Count, pageSize);
        var skip = (page - 1) * pageSize;
        if (skip >= all.Count)
        {
            return Array.Empty<T>();
        }

        return all.Skip(skip).Take(pageSize).ToList();
    }

    /// <summary>
    /// 找出"某个文件"出现在第几页（按文件路径包含关系匹配，忽略大小写）。
    /// 找不到返回 0（调用方据此提示用户）。
    /// </summary>
    public static int FindPageForFile(IReadOnlyList<string> filePaths, string fileNameFragment, int pageSize)
    {
        if (filePaths is null || filePaths.Count == 0 || string.IsNullOrWhiteSpace(fileNameFragment) || pageSize <= 0)
        {
            return 0;
        }

        var fragment = fileNameFragment.Trim();
        for (var index = 0; index < filePaths.Count; index++)
        {
            if (filePaths[index].Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return index / pageSize + 1;
            }
        }

        return 0;
    }
}
