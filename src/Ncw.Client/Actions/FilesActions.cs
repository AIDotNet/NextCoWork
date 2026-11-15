using Ncw.Client.Devices;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Actions;

/// <summary>
/// 项目文件相关的用户意图(AGENTS.md §3 规则 4:一个意图一个方法)。文件的真源在设备(经项目的执行环境访问,本机和 SSH 项目同一套);
/// 控制端不镜像文件内容:目录列表、文件内容、搜索结果都是界面的纯 UI 状态,<b>直接返回给调用方</b>,不进 Store。
/// 路径一律是<b>项目相对</b>、<c>/</c> 分隔(空串 = 项目根);越界、符号链接、修订号冲突等失败是结构化的 <see cref="ActionResult{T}"/>(<c>ErrorKey</c> 是稳定的 <c>files.*</c> key,界面翻译)。
/// 保存必须带读到的 <c>Revision</c>(乐观锁):<see cref="ActionOutcome.Conflict"/> + <c>files.conflict</c> = 文件在这之后被改过,界面让用户决定。
/// 写入和变更是 member 起的 command,每次调用带新的幂等键(重连后重发同一个意图用同一个键的需求在 <c>DeviceLink</c> 层,这里是单次意图)。
/// </summary>
public sealed class FilesActions(DeviceRegistry devices) : IFilesCommands
{
    /// <summary>列一层目录(分页、排序、可隐藏忽略表里的条目)。viewer 可用。</summary>
    public Task<ActionResult<FilesListResult>> ListAsync(string deviceId, string projectId, string path = "", string sort = "name", bool hideIgnored = false, int offset = 0, int limit = 0, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.List, new FilesListParams(projectId, path, sort, hideIgnored, offset, limit), ct));

    /// <summary>读一个文件(文本 / 图片 / 二进制分类;大文件用 <see cref="ReadRangeAsync"/> 分块)。</summary>
    public Task<ActionResult<FileContentDto>> ReadAsync(string deviceId, string projectId, string path, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.Read, new FilesReadParams(projectId, path), ct));

    /// <summary>读一段字节(第一块不带 <paramref name="revision"/>,之后带回上一块的)。</summary>
    public Task<ActionResult<FilesRangeResult>> ReadRangeAsync(string deviceId, string projectId, string path, long offset, int length, string? revision = null, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.ReadRange, new FilesReadRangeParams(projectId, path, offset, length, revision), ct));

    /// <summary>保存文本。<paramref name="revision"/> 是读到的修订号;对不上得到 <see cref="ActionOutcome.Conflict"/>。</summary>
    public Task<ActionResult<FilesWriteResult>> WriteTextAsync(string deviceId, string projectId, string path, string content, string revision, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.Write, new FilesWriteParams(projectId, path, revision, Content: content), ct, idem: Idem("write")));

    /// <summary>覆写一张已存在的图片(字节)。</summary>
    public Task<ActionResult<FilesWriteResult>> WriteBytesAsync(string deviceId, string projectId, string path, byte[] data, string revision, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.Write, new FilesWriteParams(projectId, path, revision, Data: data), ct, idem: Idem("write")));

    public Task<ActionResult<FilesMutateResult>> CreateFileAsync(string deviceId, string projectId, string path, CancellationToken ct = default) =>
        Mutate(deviceId, new FilesMutateParams(projectId, path, FileOperations.CreateFile), ct);

    public Task<ActionResult<FilesMutateResult>> CreateDirectoryAsync(string deviceId, string projectId, string path, CancellationToken ct = default) =>
        Mutate(deviceId, new FilesMutateParams(projectId, path, FileOperations.CreateDirectory), ct);

    /// <summary>改名。目标已存在得到 <c>files.exists</c>;界面问过用户之后带 <paramref name="overwrite"/> 重发(只对文件;被盖掉的先进回收目录)。</summary>
    public Task<ActionResult<FilesMutateResult>> RenameAsync(string deviceId, string projectId, string path, string destination, bool overwrite = false, CancellationToken ct = default) =>
        Mutate(deviceId, new FilesMutateParams(projectId, path, FileOperations.Rename, destination, overwrite), ct);

    public Task<ActionResult<FilesMutateResult>> MoveAsync(string deviceId, string projectId, string path, string destination, CancellationToken ct = default) =>
        Mutate(deviceId, new FilesMutateParams(projectId, path, FileOperations.Move, destination), ct);

    public Task<ActionResult<FilesMutateResult>> CopyAsync(string deviceId, string projectId, string path, string destination, CancellationToken ct = default) =>
        Mutate(deviceId, new FilesMutateParams(projectId, path, FileOperations.Copy, destination), ct);

    /// <summary>删除(移进回收目录,可恢复;结果里的 <c>RecoveryPath</c> 是它的位置)。</summary>
    public Task<ActionResult<FilesMutateResult>> DeleteAsync(string deviceId, string projectId, string path, CancellationToken ct = default) =>
        Mutate(deviceId, new FilesMutateParams(projectId, path, FileOperations.Delete), ct);

    /// <summary>输入框 <c>@</c> 的文件名建议。</summary>
    public Task<ActionResult<FilesSuggestResult>> SuggestAsync(string deviceId, string projectId, string query, int limit = 0, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.Suggest, new FilesSuggestParams(projectId, query, limit), ct));

    /// <summary>内容搜索。</summary>
    public Task<ActionResult<FilesSearchResult>> SearchAsync(string deviceId, string projectId, FilesSearchParams query, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.Search, query with { WorkspaceId = projectId }, ct));

    // ── markdown 资源(命令 / 子代理 / 模式) ──

    public Task<ActionResult<FilesResourceDto>> GetResourceAsync(string deviceId, FilesResourceRefParams reference, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.ResourceGet, reference, ct));

    /// <summary>保存资源(<c>Revision</c> 为空 = 新建)。全局作用域只有 owner。</summary>
    public Task<ActionResult<FilesResourceDto>> SaveResourceAsync(string deviceId, FilesResourceSaveParams resource, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.ResourceSave, resource, ct, idem: Idem("resource.save")));

    public Task<ActionResult<Unit>> DeleteResourceAsync(string deviceId, FilesResourceRefParams reference, CancellationToken ct = default) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.ResourceDelete, reference, ct, idem: Idem("resource.delete")));

    private Task<ActionResult<FilesMutateResult>> Mutate(string deviceId, FilesMutateParams request, CancellationToken ct) =>
        Run(deviceId, link => link.CallAsync(FilesMethods.Mutate, request, ct, idem: Idem("mutate")));

    private static string Idem(string what) => $"files.{what}:{Guid.NewGuid():N}";

    private async Task<ActionResult<T>> Run<T>(string deviceId, Func<DeviceLink, Task<T>> call)
    {
        if (devices.Find(deviceId) is not { IsConnected: true } link)
        {
            return ActionResult<T>.NotConnected();
        }

        try
        {
            return ActionResult<T>.Success(await call(link).ConfigureAwait(false));
        }
        catch (ProtocolException e)
        {
            return ActionResult<T>.From(e);
        }
    }
}
