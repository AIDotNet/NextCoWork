using Ncw.Agent.Environments;
using Ncw.Agent.Workspace.Files;

namespace Ncw.Agent.Host.Workspace;

internal sealed partial class WorkspaceToolsRuntime
{
    /// <summary>项目文件服务(<c>files.*</c>):目录、读写、变更、建议、搜索。</summary>
    public FileService Files { get; private set; } = null!;

    /// <summary>markdown 资源(命令 / 子代理 / 模式的单文件定义)的读写。</summary>
    public ResourceService Resources { get; private set; } = null!;

    partial void InitFiles()
    {
        Files = new FileService(new FileServiceOptions { Time = Clock });
        // 模式的工具名校验:内置工具 + 扩展注册的几个(Task / 计划 / 待办 / 目标 / 提问),和 2.x 的「已注册工具」同一张表。
        var known = new HashSet<string>(Conversation.Tools.Select(static t => t.Info.InternalId), StringComparer.Ordinal)
        {
            "Task", "TodoWrite", "EnterPlanMode", "ExitPlanMode", "ProposeGoal", "AskUserQuestion", "WebFetch", "web_search",
        };
        known.UnionWith(Ncw.Agent.Widgets.VisualizeTools.ToolIds);
        known.UnionWith(Ncw.Agent.Browser.BrowserTools.Names);
        Resources = new ResourceService(new ResourceOptions { IsKnownTool = known.Contains });
    }

    /// <summary>项目 → 文件服务的输入(环境 + 根)。</summary>
    public Ncw.Agent.Workspace.Files.FileProject FileProject(string workspaceId)
    {
        var project = Project(workspaceId);
        return new Ncw.Agent.Workspace.Files.FileProject(project.Project.Id, project.Environment, project.Root);
    }

    /// <summary>资源目录:全局 = 设备本机环境上的 <c>&lt;数据根&gt;/&lt;commands|agents|modes&gt;</c>;项目 = 项目环境里的 <c>&lt;项目根&gt;/.next-cowork/&lt;…&gt;</c>。</summary>
    public ResourceLocation ResourceLocationOf(string scope, string kindDirectory, string? workspaceId)
    {
        if (scope == Ncw.Protocol.Methods.ResourceScopes.Project)
        {
            var project = Project(workspaceId ?? throw new Ncw.Protocol.Wire.ProtocolException(Ncw.Protocol.Wire.ErrorCodes.InvalidRequest, "WorkspaceId is required for project resources"));
            return new ResourceLocation(project.Environment, project.Root, $".next-cowork/{kindDirectory}");
        }

        var local = Conversation.LocalEnvironment;
        var dataRoot = Path.GetDirectoryName(Database.Path) ?? throw new InvalidOperationException("The data root is unknown.");
        return new ResourceLocation(local, EnvPath.Parse(dataRoot, local.Facts.PathStyle), kindDirectory);
    }

    private partial ValueTask ProjectRemovedFilesAsync(string workspaceId)
    {
        Files.Forget(workspaceId);
        return ValueTask.CompletedTask;
    }
}
