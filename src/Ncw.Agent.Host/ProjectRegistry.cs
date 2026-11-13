using System.Text.Json;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host;

/// <summary>
/// 设备上的项目(工作区)。项目是设备持有的事实(决策 D10);控制端只镜像。
/// 同一个目录只登记一次:重复添加返回已有的项目。
/// </summary>
public sealed class ProjectRegistry
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly TimeProvider _clock;
    private List<ProjectDto> _projects;

    public ProjectRegistry(string dataRoot, TimeProvider clock)
    {
        _path = Path.Combine(dataRoot, "projects.json");
        _clock = clock;
        var file = AtomicFile.Read(_path, AgentJsonContext.Default.ProjectsFile);
        _projects = [.. file?.Projects ?? []];
        Revision = file?.Revision ?? 0;
    }

    public long Revision { get; private set; }

    /// <summary>项目列表变化后触发(在锁外)。</summary>
    public event Action<ProjectsListResult>? Changed;

    public ProjectsListResult List()
    {
        lock (_gate)
        {
            return new ProjectsListResult([.. _projects], Revision);
        }
    }

    public ProjectDto Add(string path, string? name)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "Path is required");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full))
        {
            throw new ProtocolException(ErrorCodes.NotFound, $"Folder does not exist: {full}");
        }

        ProjectDto project;
        ProjectsListResult snapshot;
        lock (_gate)
        {
            var existing = _projects.FirstOrDefault(p => string.Equals(p.Path, full, PathComparison));
            if (existing is not null)
            {
                return existing;
            }

            project = new ProjectDto(
                WorkspaceId: NewId(),
                Name: string.IsNullOrWhiteSpace(name) ? Path.GetFileName(full) is { Length: > 0 } n ? n : full : name.Trim(),
                Path: full,
                Environment: "local",
                EnvironmentName: null,
                CreatedAt: _clock.GetUtcNow().ToUnixTimeMilliseconds());
            _projects.Add(project);
            snapshot = Commit();
        }

        Changed?.Invoke(snapshot);
        return project;
    }

    public void Remove(string workspaceId)
    {
        ProjectsListResult snapshot;
        lock (_gate)
        {
            if (_projects.RemoveAll(p => p.WorkspaceId == workspaceId) == 0)
            {
                throw new ProtocolException(ErrorCodes.NotFound, $"Unknown project '{workspaceId}'");
            }

            snapshot = Commit();
        }

        Changed?.Invoke(snapshot);
    }

    private ProjectsListResult Commit()
    {
        Revision++;
        var file = new ProjectsFile(1, Revision, [.. _projects]);
        AtomicFile.Write(_path, s => JsonSerializer.Serialize(s, file, AgentJsonContext.Default.ProjectsFile));
        return new ProjectsListResult([.. _projects], Revision);
    }

    private static string NewId() => $"ws_{Guid.NewGuid():N}"[..19];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
