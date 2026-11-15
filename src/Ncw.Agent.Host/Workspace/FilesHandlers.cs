using Ncw.Agent.Environments;
using Ncw.Agent.Workspace.Files;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Agent.Host.Workspace;

/// <summary>
/// <c>files.*</c> 的方法实现(方法表在 <see cref="FilesMethods"/>)。角色、承载、幂等键由路由统一检查,这里只做业务:项目 → (环境, 根) → 文件服务 → DTO。
/// 失败一律是 <see cref="ProtocolException"/>:文件服务的 <see cref="FilesException"/> 按粗粒度错误码映射,细分 key 放进 <c>details</c>(<see cref="FilesErrorDto"/>),
/// 不回显路径或系统消息;其它异常由路由收成 <c>internal</c>。设备数据没就绪时 <paramref name="tools"/> 抛可重试的 <c>unavailable</c>。
/// </summary>
internal static class FilesHandlers
{
    public static void Register(MethodRouter router, Func<WorkspaceToolsRuntime> tools)
    {
        router.Handle(FilesMethods.List, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            var listing = await t.Files.ListAsync(t.FileProject(p.WorkspaceId), p.Path, ParseSort(p.Sort), p.HideIgnored, p.Offset, p.Limit, ct).ConfigureAwait(false);
            return new FilesListResult(
                listing.Path,
                [.. listing.Entries.Select(static e => new FileEntryDto(e.Name, e.Path, e.IsDirectory ? "dir" : "file", e.IsLink, e.Hidden, e.Ignored, e.Size, e.Mtime))],
                listing.Total, listing.Truncated);
        }).ConfigureAwait(false));

        router.Handle(FilesMethods.Read, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            return ToDto(await t.Files.ReadAsync(t.FileProject(p.WorkspaceId), p.Path, ct).ConfigureAwait(false));
        }).ConfigureAwait(false));

        router.Handle(FilesMethods.ReadRange, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            var range = await t.Files.ReadRangeAsync(t.FileProject(p.WorkspaceId), p.Path, p.Offset, p.Length, p.Revision, ct).ConfigureAwait(false);
            return new FilesRangeResult(range.Data, range.Offset, range.Length, range.Size, range.Revision, range.Eof);
        }).ConfigureAwait(false));

        router.Handle(FilesMethods.Write, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            var written = await t.Files.WriteAsync(t.FileProject(p.WorkspaceId), p.Path, p.Revision, p.Content, p.Data, ct).ConfigureAwait(false);
            return new FilesWriteResult(written.Path, written.Size, written.Revision);
        }).ConfigureAwait(false));

        router.Handle(FilesMethods.Mutate, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            var operation = ParseOperation(p.Operation);
            var result = await t.Files.MutateAsync(t.FileProject(p.WorkspaceId), operation, p.Path, p.Destination, p.Overwrite, ct).ConfigureAwait(false);
            return new FilesMutateResult(result.Path, result.Destination, result.RecoveryPath);
        }).ConfigureAwait(false));

        router.Handle(FilesMethods.Suggest, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            var result = await t.Files.SuggestAsync(t.FileProject(p.WorkspaceId), p.Query, p.Limit, ct).ConfigureAwait(false);
            return new FilesSuggestResult([.. result.Suggestions.Select(static s => new FileSuggestionDto(s.Path, s.Name))], result.Truncated);
        }).ConfigureAwait(false));

        router.Handle(FilesMethods.Search, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            var result = await t.Files.SearchAsync(t.FileProject(p.WorkspaceId), new SearchRequest(p.Pattern, p.Path, p.Glob, p.Regex, p.CaseSensitive, p.Limit), ct).ConfigureAwait(false);
            return new FilesSearchResult([.. result.Matches.Select(static m => new FileMatchDto(m.Path, m.Line, m.Column, m.Text))], result.FilesSearched, result.Truncated);
        }).ConfigureAwait(false));

        // ── markdown 资源 ──
        router.Handle(FilesMethods.ResourceGet, async (p, _, ct) => await Guard(async () =>
        {
            var t = tools();
            var kind = ParseKind(p.Kind);
            var file = await t.Resources.GetAsync(Location(t, p.Scope, kind, p.WorkspaceId), kind, p.Name, ct).ConfigureAwait(false);
            return ToDto(file, p.Scope);
        }).ConfigureAwait(false));

        router.Handle(FilesMethods.ResourceSave, async (p, ctx, ct) => await Guard(async () =>
        {
            RequireScopeRole(p.Scope, ctx);
            var t = tools();
            var kind = ParseKind(p.Kind);
            var items = p.Frontmatter.Select(static e => new FrontmatterItem(e.Key, e.Value, e.Items)).ToList();
            var file = await t.Resources.SaveAsync(Location(t, p.Scope, kind, p.WorkspaceId), kind, p.Name, items, p.Body, p.Revision, ct).ConfigureAwait(false);
            return ToDto(file, p.Scope);
        }).ConfigureAwait(false));

        router.Handle(FilesMethods.ResourceDelete, async (p, ctx, ct) => await Guard(async () =>
        {
            RequireScopeRole(p.Scope, ctx);
            var t = tools();
            var kind = ParseKind(p.Kind);
            await t.Resources.DeleteAsync(Location(t, p.Scope, kind, p.WorkspaceId), kind, p.Name, ct).ConfigureAwait(false);
            return Unit.Value;
        }).ConfigureAwait(false));
    }

    private static async ValueTask<T> Guard<T>(Func<ValueTask<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (FilesException e)
        {
            throw ToProtocol(e);
        }
        catch (EnvironmentException e)
        {
            // 服务层已经译过;漏网的环境错误(项目解析之外)同样不外泄细节
            throw ToProtocol(FilesException.From(e));
        }
    }

    /// <summary>全局作用域的资源在设备数据根里,是设备配置:写 / 删只有 owner(项目作用域 member 即可)。</summary>
    private static void RequireScopeRole(string scope, CallContext context)
    {
        if (scope == ResourceScopes.Global && context.Role < Role.Owner)
        {
            throw new ProtocolException(ErrorCodes.Forbidden, "Only an owner can change device-level resources.");
        }
    }

    private static ResourceLocation Location(WorkspaceToolsRuntime tools, string scope, ResourceKind kind, string? workspaceId)
    {
        if (scope is not (ResourceScopes.Global or ResourceScopes.Project))
        {
            throw new ProtocolException(ErrorCodes.InvalidRequest, "Unknown resource scope.");
        }

        var directory = kind switch
        {
            ResourceKind.Command => "commands",
            ResourceKind.Agent => "agents",
            _ => "modes",
        };
        return tools.ResourceLocationOf(scope, directory, workspaceId);
    }

    private static ResourceKind ParseKind(string kind) => kind switch
    {
        ResourceKinds.Command => ResourceKind.Command,
        ResourceKinds.Agent => ResourceKind.Agent,
        ResourceKinds.Mode => ResourceKind.Mode,
        _ => throw new ProtocolException(ErrorCodes.InvalidRequest, "Unknown resource kind."),
    };

    private static FileSortBy ParseSort(string sort) => sort switch
    {
        "mtime" => FileSortBy.Mtime,
        "size" => FileSortBy.Size,
        _ => FileSortBy.Name,
    };

    private static FileOperation ParseOperation(string operation) => operation switch
    {
        FileOperations.CreateFile => FileOperation.CreateFile,
        FileOperations.CreateDirectory => FileOperation.CreateDirectory,
        FileOperations.Rename => FileOperation.Rename,
        FileOperations.Move => FileOperation.Move,
        FileOperations.Copy => FileOperation.Copy,
        FileOperations.Delete => FileOperation.Delete,
        _ => throw new ProtocolException(ErrorCodes.InvalidRequest, "Unknown file operation."),
    };

    private static FileContentDto ToDto(FileContent c) => new(
        c.Kind switch { FileKind.Text => "text", FileKind.Image => "image", _ => "binary" },
        c.Path, c.Size, c.Revision, c.Text, c.Mime, c.Data, c.Chunked,
        c.Reason switch { BinaryReason.Unsupported => "unsupported", BinaryReason.TooLarge => "tooLarge", BinaryReason.Encoding => "encoding", _ => null });

    private static FilesResourceDto ToDto(ResourceFile file, string scope) => new(
        file.Kind switch { ResourceKind.Command => ResourceKinds.Command, ResourceKind.Agent => ResourceKinds.Agent, _ => ResourceKinds.Mode },
        scope, file.Name,
        [.. file.Frontmatter.Select(static i => new FrontmatterEntryDto(i.Key, i.Value, i.Items is null ? null : [.. i.Items]))],
        file.Body, [.. file.Skipped], file.Revision);

    /// <summary>文件服务的错误 → 协议错误:粗粒度码 + <c>details</c> 里的稳定 key。</summary>
    internal static ProtocolException ToProtocol(FilesException e)
    {
        var (code, key, retryable) = e.Code switch
        {
            FilesErrorCode.NotFound => (ErrorCodes.NotFound, FilesErrorKeys.NotFound, false),
            FilesErrorCode.ProjectUnavailable => (ErrorCodes.NotFound, FilesErrorKeys.ProjectUnavailable, false),
            FilesErrorCode.Exists => (ErrorCodes.Conflict, FilesErrorKeys.Exists, false),
            FilesErrorCode.Conflict => (ErrorCodes.Conflict, FilesErrorKeys.Conflict, false),
            FilesErrorCode.InvalidPath => (ErrorCodes.InvalidRequest, FilesErrorKeys.InvalidPath, false),
            FilesErrorCode.InvalidEncoding => (ErrorCodes.InvalidRequest, FilesErrorKeys.InvalidEncoding, false),
            FilesErrorCode.NotFile => (ErrorCodes.InvalidRequest, FilesErrorKeys.NotFile, false),
            FilesErrorCode.InvalidName => (ErrorCodes.InvalidRequest, FilesErrorKeys.InvalidName, false),
            FilesErrorCode.InvalidPattern => (ErrorCodes.InvalidRequest, FilesErrorKeys.InvalidPattern, false),
            FilesErrorCode.InvalidResource => (ErrorCodes.InvalidRequest, FilesErrorKeys.InvalidResource, false),
            FilesErrorCode.Symlink => (ErrorCodes.Forbidden, FilesErrorKeys.Symlink, false),
            FilesErrorCode.Permission => (ErrorCodes.Forbidden, FilesErrorKeys.Permission, false),
            FilesErrorCode.BuiltinReadOnly => (ErrorCodes.Forbidden, FilesErrorKeys.BuiltinReadOnly, false),
            FilesErrorCode.TooLarge => (ErrorCodes.TooLarge, FilesErrorKeys.TooLarge, false),
            FilesErrorCode.Unsupported => (ErrorCodes.Unsupported, FilesErrorKeys.Unsupported, false),
            FilesErrorCode.Disconnected => (ErrorCodes.Unavailable, FilesErrorKeys.Disconnected, true),
            _ => (ErrorCodes.Internal, FilesErrorKeys.Io, false),
        };
        // Io 不泄漏细节(路径、系统消息);其它码的 Detail 是服务层写的英文短句,不含路径
        var details = FrameCodec.EncodePayload(new FilesErrorDto(key, code == ErrorCodes.Internal ? null : e.Detail), PayloadShapes.GetShape<FilesErrorDto>());
        return new ProtocolException(code, key, retryable, details);
    }
}
