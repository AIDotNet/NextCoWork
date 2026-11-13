using Ncw.Agent.Storage;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host;

/// <summary>
/// 设备上的项目 = 2.x 的工作区(<c>workspaces</c> 表,决策 D13)。项目是设备持有的事实(决策 D10);控制端只镜像。
/// 同一个目录只登记一次:重复添加返回已有的项目。
/// </summary>
public sealed class ProjectRegistry(WorkspaceRepository workspaces)
{
    private long _revision;

    /// <summary>项目列表的版本号(进程内递增,用于 device 主题)。</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>项目列表变化后触发。</summary>
    public event Action<ProjectsListResult>? Changed;

    public ProjectsListResult List() =>
        new([.. workspaces.List().Select(ToDto)], Revision);

    public ProjectDto Add(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "Path is required");
        }

        if (!Directory.Exists(path))
        {
            throw new ProtocolException(ErrorCodes.NotFound, $"Folder does not exist: {path}");
        }

        var before = workspaces.List().Count;
        var record = workspaces.AddLocal(PathCanon.RealPath(path), name);
        if (workspaces.List().Count != before)
        {
            Publish();
        }

        return ToDto(record);
    }

    public void Remove(string workspaceId)
    {
        if (!workspaces.Remove(workspaceId))
        {
            throw new ProtocolException(ErrorCodes.NotFound, $"Unknown project '{workspaceId}'");
        }

        Publish();
    }

    private void Publish()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke(List());
    }

    private ProjectDto ToDto(WorkspaceRecord w) => new(
        w.Id,
        w.Name,
        w.RootPath,
        w.EnvironmentKind == "connection" ? "ssh" : "local",
        w.ConnectionId is { } id ? workspaces.ConnectionName(id) ?? id : null,
        w.CreatedAt);
}
