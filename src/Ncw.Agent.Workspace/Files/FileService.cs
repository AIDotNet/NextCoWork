using Ncw.Agent.Environments;
using Ncw.Core.Domain;

namespace Ncw.Agent.Workspace.Files;

/// <summary>
/// 项目文件服务(<c>files.*</c> 的领域层,移植自 2.x 的 <c>services/workspace-files.ts</c>、<c>workspace-listing.ts</c>、<c>workspace-range.ts</c>、<c>workspace-file-content.ts</c>、<c>ipc/workspace-search.ts</c>)。
/// <list type="bullet">
/// <item>所有文件访问经项目的 <see cref="IExecutionEnvironment"/>(本机和 SSH 项目同一套行为);路径是项目相对、<c>/</c> 分隔,边界保护见 <see cref="ProjectPaths"/>。</item>
/// <item>错误一律是 <see cref="FilesException"/>(码 + 英文诊断,不含路径),本层不产出界面文字。</item>
/// <item>读写不用「先 stat 再 open」拼内容:读取时核对大小,中途被改写得 <see cref="FilesErrorCode.Conflict"/>,不返回混合版本的内容。</item>
/// </list>
/// 实现按操作拆成分部类:<c>FileService.List / Read / Write / Mutate / Suggest / Search</c>。
/// </summary>
public sealed partial class FileService(FileServiceOptions? options = null)
{
    private readonly FileServiceOptions _options = options ?? new FileServiceOptions();
    private readonly Lock _indexGate = new();
    private readonly Dictionary<string, SuggestIndex> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<SuggestIndex>> _building = new(StringComparer.Ordinal);

    /// <summary>项目被移除 / 换根时丢掉它的建议索引:留着只是白占内存。</summary>
    public void Forget(string projectId)
    {
        var prefix = projectId + "\n";
        lock (_indexGate)
        {
            foreach (var key in _indexes.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _indexes.Remove(key);
            }

            foreach (var key in _building.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _building.Remove(key);
            }
        }
    }

    /// <summary>把环境层的异常统一译成 <see cref="FilesException"/>(取消不译)。</summary>
    private static async ValueTask<T> Guard<T>(Func<ValueTask<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (Exception e) when (e is EnvironmentException or FilesException)
        {
            throw FilesException.From(e);
        }
    }

    private static string ExtensionOf(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var dot = name.LastIndexOf('.');
        return dot <= 0 ? "" : name[dot..].ToLowerInvariant();
    }

    private static async ValueTask<byte[]> ReadAllAsync(Stream stream, long expected, CancellationToken ct)
    {
        // 最多读 expected + 1 字节:多出来说明文件在读的时候变长了
        var buffer = new byte[checked((int)Math.Min(expected + 1, int.MaxValue - 64))];
        var used = 0;
        while (used < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(used), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            used += read;
        }

        if (used != expected)
        {
            throw new FilesException(FilesErrorCode.Conflict, "the file changed while it was being read");
        }

        return used == buffer.Length ? buffer : buffer[..used];
    }
}
