namespace Ncw.Client.ViewModels;

/// <summary>右侧面板的页签。</summary>
public enum InspectorTabKind
{
    /// <summary>文件管理器:默认页签,总是存在,不能关闭。</summary>
    Files,
    /// <summary>Git 变更:通过「+」添加。</summary>
    Git,
    /// <summary>上下文与待办:通过「+」添加。</summary>
    Context,
    /// <summary>历史:通过「+」添加。</summary>
    History,
    /// <summary>子代理:当前会话有子代理时自动出现,没有时自动消失。</summary>
    Subagents,
}

/// <summary>右侧面板页签的组成规则(纯函数,单测见 Ncw.Client.Tests)。</summary>
public static class InspectorTabs
{
    /// <summary>可以通过「+」添加的页签,按菜单顺序。</summary>
    public static readonly IReadOnlyList<InspectorTabKind> Addable =
        [InspectorTabKind.Git, InspectorTabKind.Context, InspectorTabKind.History];

    /// <summary>显示顺序:文件 → 用户添加的(按添加顺序)→ 子代理(有才显示)。</summary>
    public static IReadOnlyList<InspectorTabKind> Compose(IEnumerable<InspectorTabKind> added, bool hasSubagents)
    {
        var tabs = new List<InspectorTabKind> { InspectorTabKind.Files };
        foreach (var kind in added)
        {
            if (Addable.Contains(kind) && !tabs.Contains(kind))
            {
                tabs.Add(kind);
            }
        }

        if (hasSubagents)
        {
            tabs.Add(InspectorTabKind.Subagents);
        }

        return tabs;
    }

    /// <summary>「+」菜单里还能添加的页签。</summary>
    public static IReadOnlyList<InspectorTabKind> Remaining(IEnumerable<InspectorTabKind> added)
    {
        var set = added.ToHashSet();
        return Addable.Where(k => !set.Contains(k)).ToList();
    }

    public static string Title(InspectorTabKind kind) => kind switch
    {
        InspectorTabKind.Files => "文件",
        InspectorTabKind.Git => "Git",
        InspectorTabKind.Context => "上下文",
        InspectorTabKind.History => "历史",
        InspectorTabKind.Subagents => "子代理",
        _ => kind.ToString(),
    };
}
