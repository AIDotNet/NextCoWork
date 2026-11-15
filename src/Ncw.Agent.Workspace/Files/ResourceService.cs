using System.Text;
using Ncw.Agent.Environments;
using Ncw.Core.Text;

namespace Ncw.Agent.Workspace.Files;

public enum ResourceKind
{
    Command,
    Agent,
    Mode,
}

/// <summary>前置块的一项:字符串值或列表值。</summary>
public sealed record FrontmatterItem(string Key, string? Value = null, IReadOnlyList<string>? Items = null);

/// <summary>一个 markdown 资源的内容。<see cref="Revision"/> 是不透明的版本串(修改时间刻度 + 大小),保存时带回。</summary>
public sealed record ResourceFile(ResourceKind Kind, string Name, IReadOnlyList<FrontmatterItem> Frontmatter, string Body, IReadOnlyList<string> Skipped, string Revision);

/// <summary>资源目录在哪:<see cref="Root"/> 下的 <see cref="Directory"/>(<c>/</c> 分隔,相对)。全局作用域是 <c>(设备本机环境, 数据根, "commands")</c>,项目作用域是 <c>(项目环境, 项目根, ".next-cowork/commands")</c>。</summary>
public sealed record ResourceLocation(IExecutionEnvironment Environment, EnvPath Root, string Directory);

public sealed class ResourceOptions
{
    /// <summary>模式的工具名校验(mode 的 <c>tools</c> / <c>requiredTools</c> 必须是已知工具)。null = 不校验。</summary>
    public Func<string, bool>? IsKnownTool { get; init; }
}

/// <summary>
/// markdown 资源的单文件读写(移植自 2.x 的 <c>kernel/markdown-resource.ts</c> + <c>ipc/markdown-resource.ts</c>):命令 / 子代理 / 模式在磁盘上同构(<c>&lt;name&gt;.md</c>,frontmatter + 正文)。
/// <list type="bullet">
/// <item><b>保真</b>:前置块原样进原样出,包括本应用不认识的键(表单只覆盖认识的那几个,从 Claude Code 粘来的 <c>color: blue</c> 不能被编辑一次就抹掉);
/// 它和加载器(扫描器)是两件事 —— 拿加载结果回写等于把兜底值写进用户的文件。键序由种类决定且稳定(每存一次换一次顺序,git diff 会变成一片噪音)。</item>
/// <item><b>并发保护</b>:带 revision 的是覆盖,对不上 <see cref="FilesErrorCode.Conflict"/>;不带的是新建,文件已存在同样算冲突(「新建」不该悄悄覆盖)。</item>
/// <item><b>两道闸</b>:名字规则(挡 <c>../</c>、绝对路径、空串、超长)+ <see cref="PathGuard"/>(挡符号链接逃逸:<c>commands/x.md -&gt; ~/.ssh/id_rsa</c> 名字合法,只有解析真实路径才看得出来)。少任何一道,一次「删除」就能删到目录外面去。</item>
/// <item>解析器读不懂的语法(两层以上嵌套、锚点)记进 <see cref="ResourceFile.Skipped"/>:值在解析阶段就没了,保存会丢,控制端必须当面说清楚。</item>
/// </list>
/// </summary>
public sealed class ResourceService(ResourceOptions? options = null)
{
    /// <summary>正文上限(字符):渲染层的限制挡不住直调协议,所以设备再兜一次。</summary>
    public const int BodyMax = 64 * 1024;

    private const int FileMaxBytes = 128 * 1024;

    private static readonly string[] BuiltinModes = ["code", "plan", "acp"];

    private readonly ResourceOptions _options = options ?? new ResourceOptions();

    public static bool IsBuiltinMode(string name) => BuiltinModes.Contains(name, StringComparer.Ordinal);

    /// <summary>名字规则:命令 <c>^[a-z0-9][a-z0-9_-]{0,63}$</c>(不分大小写),子代理 / 模式 <c>^[a-z0-9][a-z0-9-]{0,63}$</c>。</summary>
    public static bool IsValidName(ResourceKind kind, string name)
    {
        if (name.Length is 0 or > 64 || !IsAsciiLetterOrDigit(name[0], kind == ResourceKind.Command))
        {
            return false;
        }

        foreach (var c in name)
        {
            var ok = IsAsciiLetterOrDigit(c, kind == ResourceKind.Command) || c == '-' || (kind == ResourceKind.Command && c == '_');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetterOrDigit(char c, bool allowUpper) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'z' || (allowUpper && c is >= 'A' and <= 'Z');

    private static IReadOnlyList<string> KeyOrder(ResourceKind kind) => kind switch
    {
        ResourceKind.Command => ["description", "argument-hint"],
        ResourceKind.Agent => ["name", "description", "tools", "model", "modelProviderId", "permissionMode", "color"],
        _ => ["name", "description", "tools", "requiredTools"],
    };

    /// <summary>读一个资源。文件不存在 <see cref="FilesErrorCode.NotFound"/>(那是「还没建」)。</summary>
    public async ValueTask<ResourceFile> GetAsync(ResourceLocation location, ResourceKind kind, string name, CancellationToken ct = default)
    {
        try
        {
            var file = await ResolveAsync(location, kind, name, ct).ConfigureAwait(false);
            var fs = location.Environment.Fs;
            if (!await fs.ExistsAsync(file, ct).ConfigureAwait(false))
            {
                throw new FilesException(FilesErrorCode.NotFound, "resource does not exist");
            }

            var bytes = await fs.ReadBytesAsync(file, FileMaxBytes, ct).ConfigureAwait(false);
            var parsed = FrontmatterParser.Parse(Encoding.UTF8.GetString(bytes));
            return new ResourceFile(
                kind, name,
                [.. parsed.Data.Select(static kv => kv.Value.IsList ? new FrontmatterItem(kv.Key, Items: kv.Value.Items) : new FrontmatterItem(kv.Key, kv.Value.Text))],
                parsed.Body, [.. parsed.Skipped], await RevisionAsync(fs, file, ct).ConfigureAwait(false));
        }
        catch (EnvironmentException e)
        {
            throw Translate(e);
        }
    }

    /// <summary>保存(新建或覆盖)。<paramref name="revision"/> 为 null = 新建。</summary>
    public async ValueTask<ResourceFile> SaveAsync(
        ResourceLocation location, ResourceKind kind, string name, IReadOnlyList<FrontmatterItem> frontmatter, string body, string? revision, CancellationToken ct = default)
    {
        try
        {
            if (kind == ResourceKind.Mode)
            {
                ValidateMode(name, frontmatter, body);
            }

            var file = await ResolveAsync(location, kind, name, ct).ConfigureAwait(false);
            var fs = location.Environment.Fs;
            var exists = await fs.ExistsAsync(file, ct).ConfigureAwait(false);
            if (revision is null ? exists : !exists || await RevisionAsync(fs, file, ct).ConfigureAwait(false) != revision)
            {
                throw new FilesException(FilesErrorCode.Conflict, "the resource changed or already exists");
            }

            var data = new Dictionary<string, FrontmatterValue?>(StringComparer.Ordinal);
            foreach (var item in frontmatter)
            {
                if (item.Key.Length == 0)
                {
                    continue;
                }

                data[item.Key] = item.Items is { } list ? FrontmatterValue.FromList(list) : FrontmatterValue.FromText(item.Value ?? "");
            }

            var clamped = body.Length > BodyMax ? body[..BodyMax] : body;
            var text = FrontmatterParser.Serialize(data, clamped, new FrontmatterSerializeOptions { Order = KeyOrder(kind) });
            await fs.MkdirAsync(file.Parent, recursive: true, ct).ConfigureAwait(false);
            using var source = new MemoryStream(Encoding.UTF8.GetBytes(text));
            try
            {
                _ = await fs.WriteStreamAsync(file, source, new FsWriteStreamOptions(FileMaxBytes * 4L, Exclusive: revision is null), ct).ConfigureAwait(false);
            }
            catch (EnvironmentException e) when (e.Code == EnvironmentErrorCode.AlreadyExists)
            {
                throw new FilesException(FilesErrorCode.Conflict, "the resource was created concurrently", e);
            }

            return await GetAsync(location, kind, name, ct).ConfigureAwait(false);
        }
        catch (EnvironmentException e)
        {
            throw Translate(e);
        }
    }

    /// <summary>删除一个资源。幂等:不存在也不报错。</summary>
    public async ValueTask DeleteAsync(ResourceLocation location, ResourceKind kind, string name, CancellationToken ct = default)
    {
        try
        {
            if (kind == ResourceKind.Mode && IsBuiltinMode(name))
            {
                throw new FilesException(FilesErrorCode.BuiltinReadOnly, "built-in modes are read-only");
            }

            var file = await ResolveAsync(location, kind, name, ct).ConfigureAwait(false);
            var fs = location.Environment.Fs;
            if (await fs.ExistsAsync(file, ct).ConfigureAwait(false))
            {
                await fs.DeleteFileAsync(file, ct).ConfigureAwait(false);
            }
        }
        catch (EnvironmentException e)
        {
            throw Translate(e);
        }
    }

    private static async ValueTask<EnvPath> ResolveAsync(ResourceLocation location, ResourceKind kind, string name, CancellationToken ct)
    {
        if (!IsValidName(kind, name))
        {
            throw new FilesException(FilesErrorCode.InvalidName, "bad resource name");
        }

        return await location.Environment.Paths.ResolveWithinAsync(location.Root, $"{location.Directory}/{name}.md", ct).ConfigureAwait(false);
    }

    /// <summary>修改时间刻度 + 大小:一次 stat 就能判断「我读到的还是不是磁盘上那一份」。</summary>
    private static async ValueTask<string> RevisionAsync(IEnvironmentFs fs, EnvPath file, CancellationToken ct)
    {
        var info = await fs.StatAsync(file, ct).ConfigureAwait(false);
        return $"{info.ModifiedAt.UtcTicks}:{info.Size}";
    }

    private static FilesException Translate(EnvironmentException e) =>
        // 链接逃逸:名字合法、文件却指向目录外
        e.Code == EnvironmentErrorCode.PathEscape ? new FilesException(FilesErrorCode.Permission, "the resource path leaves its directory", e) : FilesException.From(e);

    private static IReadOnlyList<string>? ListOf(FrontmatterItem? item) =>
        item is null ? null
        : item.Items is { } items ? [.. items.Select(static i => i.Trim()).Where(static i => i.Length > 0)]
        : [.. (item.Value ?? "").Split(',').Select(static i => i.Trim()).Where(static i => i.Length > 0)];

    private void ValidateMode(string name, IReadOnlyList<FrontmatterItem> frontmatter, string body)
    {
        if (IsBuiltinMode(name))
        {
            throw new FilesException(FilesErrorCode.BuiltinReadOnly, "built-in modes are read-only");
        }

        FrontmatterItem? Find(string key) => frontmatter.FirstOrDefault(i => i.Key == key);
        static bool Blank(FrontmatterItem? i) => i?.Value is not { } v || v.Trim().Length == 0;
        if (Blank(Find("name")))
        {
            throw new FilesException(FilesErrorCode.InvalidResource, "Mode name is required.");
        }

        if (Blank(Find("description")))
        {
            throw new FilesException(FilesErrorCode.InvalidResource, "Mode description is required.");
        }

        if (body.Trim().Length == 0)
        {
            throw new FilesException(FilesErrorCode.InvalidResource, "Mode prompt is required.");
        }

        var toolsItem = Find("tools");
        var requiredItem = Find("requiredTools");
        var tools = ListOf(toolsItem);
        var required = ListOf(requiredItem);
        if (toolsItem is not null && tools is not { Count: > 0 })
        {
            throw new FilesException(FilesErrorCode.InvalidResource, "Mode tools must be a non-empty list.");
        }

        if (requiredItem is not null && required is not { Count: > 0 })
        {
            throw new FilesException(FilesErrorCode.InvalidResource, "Mode required tools must be a non-empty list.");
        }

        if (required is not null)
        {
            if (tools is null)
            {
                throw new FilesException(FilesErrorCode.InvalidResource, "Required tools must also be listed in tools.");
            }

            var missing = required.Where(t => !tools.Contains(t)).ToList();
            if (missing.Count > 0)
            {
                throw new FilesException(FilesErrorCode.InvalidResource, $"Required tools must also be listed in tools: {string.Join(", ", missing)}");
            }
        }

        if (_options.IsKnownTool is { } known)
        {
            var unknown = (tools ?? []).Concat(required ?? []).Where(t => !known(t)).Distinct().ToList();
            if (unknown.Count > 0)
            {
                throw new FilesException(FilesErrorCode.InvalidResource, $"Unknown mode tools: {string.Join(", ", unknown)}");
            }
        }
    }
}
