using LimbusTranslator.Infrastructure.Presentation;
using Xunit;

namespace LimbusTranslator.Core.Tests;

/// <summary>
/// 第9.0C.15轮：**文件选择的子集操作**语义（右键菜单 / Ctrl+Shift 多选的后端保证）。
///
/// 要求：对"选中的那几行"执行 选中 / 取消 / 反选，**绝不能影响没选中的文件**，
/// 也不能因为视图过滤（任务范围 / 搜索）而丢掉其它范围里的选择。
/// </summary>
public sealed class FileSelectionSubsetTests
{
    private static FileSelectionState NewState(params string[] files)
    {
        var state = new FileSelectionState();
        state.SelectAll(files);
        return state;
    }

    [Fact]
    public void 子集取消_只影响这些文件()
    {
        var state = NewState("A.json", "B.json", "C.json");

        state.SelectNone(new[] { "B.json" });

        Assert.True(state.IsSelected("A.json"));
        Assert.False(state.IsSelected("B.json"));
        Assert.True(state.IsSelected("C.json"));
    }

    [Fact]
    public void 子集重新选中_只影响这些文件()
    {
        var state = new FileSelectionState();
        state.SelectNone(new[] { "A.json", "B.json", "C.json" });

        state.SelectAll(new[] { "A.json", "C.json" });

        Assert.True(state.IsSelected("A.json"));
        Assert.False(state.IsSelected("B.json"));
        Assert.True(state.IsSelected("C.json"));
    }

    [Fact]
    public void 子集反选_只翻转这些文件()
    {
        var state = new FileSelectionState();
        state.SelectAll(new[] { "A.json", "C.json" });
        state.SelectNone(new[] { "B.json", "D.json" });

        state.Invert(new[] { "A.json", "B.json" });

        Assert.False(state.IsSelected("A.json"));   // true → false
        Assert.True(state.IsSelected("B.json"));    // false → true
        Assert.True(state.IsSelected("C.json"));    // 未被操作
        Assert.False(state.IsSelected("D.json"));   // 未被操作
    }

    [Fact]
    public void 未操作过的文件默认视为选中_保持旧行为()
    {
        var state = new FileSelectionState();

        state.SelectNone(new[] { "A.json" });

        Assert.True(state.IsSelected("NeverTouched.json"));   // 默认全选语义（9.0C.3 兼容要求）
        Assert.False(state.IsSelected("A.json"));
    }
}
