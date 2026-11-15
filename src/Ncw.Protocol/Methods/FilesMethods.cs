using PolyType;

namespace Ncw.Protocol.Methods;

/// <summary>
/// 项目文件的方法表(docs/design/09 §8.4,功能盘点 10 的 <c>workspace</c> 文件部分与 <c>resource</c> 模块)。能力 <see cref="Capabilities.Files"/>,承载 本机 + 远程。
/// 读取(列目录、读、范围读、搜索、资源读取)viewer 起;写入与变更 member 起(command 带幂等键);markdown 资源的全局作用域写入还要 owner(handler 里检查)。
/// <para>
/// <b>路径</b>:一律是<b>项目相对路径</b>,分隔符用 <c>/</c>,空串 = 项目根;不接受 <c>..</c> / <c>.</c> / 空段、反斜杠、盘符。
/// 设备经项目的执行环境(<c>IExecutionEnvironment</c>)访问文件,并按路径守卫(<c>PathGuard</c>)拒绝越界(<c>invalid_request</c> / <c>forbidden</c>),本机和 SSH 项目同一套行为。
/// 响应里永远不出现设备上的绝对路径。
/// </para>
/// <para>
/// <b>大文件</b>:协议没有 stream 种类(09 §8.1 的 stream 留给以后),单帧上限 4 MiB,所以 <c>files.read</c> 只内联不超过 <see cref="FilesLimits.TextMax"/> 的文本和
/// 不超过 <see cref="FilesLimits.InlineImageMax"/> 的图片,更大的内容用 <c>files.readRange</c> 按 <see cref="FilesLimits.RangeMax"/> 分块读(带 <c>Revision</c>,中途文件变了得到 <c>conflict</c>)。
/// </para>
/// <para>
/// <b>错误</b>:粗粒度错误码(<c>not_found</c> / <c>conflict</c> / <c>forbidden</c> / <c>invalid_request</c> / <c>too_large</c> / <c>unavailable</c> / <c>unsupported</c>),
/// 细分原因放在 <c>error.details</c>(<see cref="FilesErrorDto"/>,稳定 key <c>files.*</c>,控制端翻译),不回显路径或系统消息。
/// </para>
/// </summary>
public static class FilesMethods
{
    public static readonly MethodDef<FilesListParams, FilesListResult> List = Query<FilesListParams, FilesListResult>("files.list", Role.Viewer);

    public static readonly MethodDef<FilesReadParams, FileContentDto> Read = Query<FilesReadParams, FileContentDto>("files.read", Role.Viewer);

    public static readonly MethodDef<FilesReadRangeParams, FilesRangeResult> ReadRange = Query<FilesReadRangeParams, FilesRangeResult>("files.readRange", Role.Viewer);

    public static readonly MethodDef<FilesWriteParams, FilesWriteResult> Write = Command<FilesWriteParams, FilesWriteResult>("files.write", Role.Member);

    public static readonly MethodDef<FilesMutateParams, FilesMutateResult> Mutate = Command<FilesMutateParams, FilesMutateResult>("files.mutate", Role.Member);

    /// <summary>输入框 <c>@</c> 的文件名建议(模糊匹配排序,设备上算,只回十几条)。</summary>
    public static readonly MethodDef<FilesSuggestParams, FilesSuggestResult> Suggest = Query<FilesSuggestParams, FilesSuggestResult>("files.suggest", Role.Viewer);

    /// <summary>内容搜索(字面或正则,跳过依赖 / 构建目录和二进制文件)。</summary>
    public static readonly MethodDef<FilesSearchParams, FilesSearchResult> Search = Query<FilesSearchParams, FilesSearchResult>("files.search", Role.Viewer);

    /// <summary>读一个 markdown 资源(命令 / 子代理 / 模式的单文件定义:frontmatter + 正文)。</summary>
    public static readonly MethodDef<FilesResourceRefParams, FilesResourceDto> ResourceGet = Query<FilesResourceRefParams, FilesResourceDto>("files.resource.get", Role.Viewer);

    /// <summary>保存(新建或覆盖)一个 markdown 资源。带 <c>Revision</c> 的是覆盖(对不上 <c>conflict</c>),不带的是新建(已存在 <c>conflict</c>)。全局作用域只有 owner。</summary>
    public static readonly MethodDef<FilesResourceSaveParams, FilesResourceDto> ResourceSave = Command<FilesResourceSaveParams, FilesResourceDto>("files.resource.save", Role.Member);

    /// <summary>删除一个 markdown 资源(幂等:不存在也不报错)。全局作用域只有 owner。</summary>
    public static readonly MethodDef<FilesResourceRefParams, Unit> ResourceDelete = Command<FilesResourceRefParams, Unit>("files.resource.delete", Role.Member);

    public static readonly IReadOnlyList<MethodDef> All = [List, Read, ReadRange, Write, Mutate, Suggest, Search, ResourceGet, ResourceSave, ResourceDelete];

    private static MethodDef<TP, TR> Query<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Query, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Files };

    private static MethodDef<TP, TR> Command<TP, TR>(string name, Role role) =>
        new(name, MethodKind.Command, role, Transports.Any, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = Capabilities.Files };
}

/// <summary>文件方法的尺寸约定(设备和控制端共用,避免两边各写一个数)。</summary>
public static class FilesLimits
{
    /// <summary>内联文本上限(2 MiB,和 2.x 一致);更大的文本 <see cref="FileContentDto.Kind"/> = <c>binary</c>、<c>Reason</c> = <c>tooLarge</c>,用 <c>files.readRange</c> 分块读。</summary>
    public const int TextMax = 2 * 1024 * 1024;

    /// <summary>内联图片上限(3 MiB,留出帧开销);更大的图片 <c>Data</c> 为空、<c>Chunked</c> 为真,用 <c>files.readRange</c> 分块读。</summary>
    public const int InlineImageMax = 3 * 1024 * 1024;

    /// <summary>图片文件总大小上限(16 MiB,和 2.x 一致);更大的当作 <c>binary</c> / <c>tooLarge</c>。</summary>
    public const int ImageMax = 16 * 1024 * 1024;

    /// <summary><c>files.readRange</c> 单块上限(1 MiB):超过按上限截。</summary>
    public const int RangeMax = 1024 * 1024;

    /// <summary>目录列表单页缺省条数(2.x 的 <c>DIR_LISTING_LIMIT</c>)与上限。</summary>
    public const int ListDefault = 2000;

    public const int ListMax = 5000;

    public const int SuggestDefault = 20;

    public const int SuggestMax = 50;

    public const int SearchDefault = 200;

    public const int SearchMax = 1000;
}

/// <summary>文件方法的稳定错误 key(<see cref="FilesErrorDto.Key"/>);控制端翻译,设备不产出界面文字。</summary>
public static class FilesErrorKeys
{
    public const string NotFound = "files.notFound";
    public const string Exists = "files.exists";
    public const string Conflict = "files.conflict";
    public const string InvalidPath = "files.invalidPath";
    public const string Symlink = "files.symlink";
    public const string Permission = "files.permission";
    public const string TooLarge = "files.tooLarge";
    public const string NotFile = "files.notFile";
    public const string InvalidEncoding = "files.invalidEncoding";
    public const string Unsupported = "files.unsupported";
    public const string Io = "files.io";
    public const string ProjectUnavailable = "files.projectUnavailable";
    public const string InvalidPattern = "files.invalidPattern";
    public const string InvalidName = "files.invalidName";
    public const string InvalidResource = "files.invalidResource";
    public const string BuiltinReadOnly = "files.builtinReadOnly";
    public const string Disconnected = "files.disconnected";
}

/// <summary><c>error.details</c> 的结构化补充(只增键,未知键忽略)。<see cref="Detail"/> 是英文诊断,只给日志,不含路径。</summary>
public sealed record FilesErrorDto(string Key, string? Detail = null);

// ───────────────────────── 目录 ─────────────────────────

/// <summary>
/// 列一层目录(不递归)。结果按「目录在前,各自再按 <see cref="Sort"/> 排」,排完再按 <see cref="Offset"/> / <see cref="Limit"/> 分页。
/// </summary>
/// <param name="Path">项目相对;空串 = 项目根。</param>
/// <param name="Sort"><c>name</c>(缺省)/ <c>mtime</c>(新的在前)/ <c>size</c>(大的在前);平局回落到名字。</param>
/// <param name="HideIgnored">为真时去掉忽略表里的条目(<c>.git</c>、<c>node_modules</c>、<c>dist</c>……整张表见 <c>IgnoreList</c>,不读 <c>.gitignore</c>);缺省假:全列,<see cref="FileEntryDto.Ignored"/> 标出来让界面自己筛。</param>
/// <param name="Limit">0 = 缺省 <see cref="FilesLimits.ListDefault"/>,上限 <see cref="FilesLimits.ListMax"/>。</param>
public sealed record FilesListParams(string WorkspaceId, string Path = "", string Sort = "name", bool HideIgnored = false, int Offset = 0, int Limit = 0);

/// <summary>目录里的一项。<see cref="Path"/> 是项目相对、<c>/</c> 分隔(树的展开 / 选中 / 页签三处共用的 key)。</summary>
/// <param name="Kind"><c>dir</c> / <c>file</c>。指向目录的符号链接是 <c>dir</c>(树里能展开),指向文件或断链是 <c>file</c>。</param>
/// <param name="Link">本身是符号链接(界面可以加角标;读写时设备按链接策略处理)。</param>
/// <param name="Hidden">名字以 <c>.</c> 开头。</param>
/// <param name="Ignored">命中忽略表。</param>
/// <param name="Size">文件大小;目录没有。</param>
/// <param name="Mtime">修改时间(Unix 毫秒);拿不到没有。</param>
public sealed record FileEntryDto(string Name, string Path, string Kind, bool Link, bool Hidden, bool Ignored, long? Size = null, long? Mtime = null);

/// <param name="Total">这一层过滤后的总条数(不受分页影响)。</param>
/// <param name="Truncated">本页之后还有(<c>Offset + Entries.Length &lt; Total</c>)。</param>
public sealed record FilesListResult(string Path, FileEntryDto[] Entries, int Total, bool Truncated);

// ───────────────────────── 读 / 写 ─────────────────────────

public sealed record FilesReadParams(string WorkspaceId, string Path);

/// <summary>
/// 一个文件的内容。<see cref="Kind"/>:<c>text</c>(UTF-8 原文,保留 BOM 和换行,<see cref="Content"/>)/ <c>image</c>(<see cref="Mime"/>,<see cref="Data"/> 是字节;
/// 超过内联上限时 <see cref="Chunked"/> 为真、<see cref="Data"/> 为空)/ <c>binary</c>(<see cref="Reason"/>:<c>unsupported</c> / <c>tooLarge</c> / <c>encoding</c>)。
/// <see cref="Revision"/> 是内容摘要(SHA-256 十六进制;过大没读的是 <c>large:&lt;size&gt;:&lt;mtime&gt;</c>),保存必须带回它,防止盖掉外部的修改。
/// </summary>
public sealed record FileContentDto(
    string Kind,
    string Path,
    long Size,
    string Revision,
    string? Content = null,
    string? Mime = null,
    byte[]? Data = null,
    bool Chunked = false,
    string? Reason = null);

/// <param name="Offset">起始字节。</param>
/// <param name="Length">想读多少,超过 <see cref="FilesLimits.RangeMax"/> 按上限。</param>
/// <param name="Revision">上一块拿到的版本;和现在对不上 <c>conflict</c>(文件在分块读的中途变了)。第一块不带。</param>
public sealed record FilesReadRangeParams(string WorkspaceId, string Path, long Offset, int Length, string? Revision = null);

/// <param name="Length">实际返回的字节数(越过文件尾时可能为 0)。</param>
/// <param name="Revision"><c>large:&lt;size&gt;:&lt;mtime&gt;</c>,后续块带回它。</param>
public sealed record FilesRangeResult(byte[] Data, long Offset, int Length, long Size, string Revision, bool Eof);

/// <summary>
/// 覆写一个已存在的文件。<see cref="Content"/>(UTF-8 文本,拒绝二进制文本)和 <see cref="Data"/>(字节,只允许覆写已经是图片的文件)二选一。
/// <see cref="Revision"/> 对不上 <c>conflict</c>。写入是同目录临时文件 + 原子替换,保存中断不会留下被截断的文件。
/// </summary>
public sealed record FilesWriteParams(string WorkspaceId, string Path, string Revision, string? Content = null, byte[]? Data = null);

public sealed record FilesWriteResult(string Path, long Size, string Revision);

// ───────────────────────── 变更 ─────────────────────────

/// <summary>
/// 新建 / 改名 / 移动 / 复制 / 删除。<see cref="Operation"/>:<c>createFile</c> / <c>createDirectory</c> / <c>rename</c> / <c>move</c> / <c>copy</c> / <c>delete</c>。
/// <see cref="Destination"/> 是 rename / move / copy 的完整项目相对目标路径。目标已存在 <c>conflict</c>(<c>files.exists</c>)。
/// <see cref="Overwrite"/> <b>只对 rename 生效</b>(且目标必须是文件):被盖掉的文件先进回收目录再改名,不是直接覆盖。
/// <b>删除不是永久删除</b>:目标被移进项目根下 <c>.next-cowork/trash/</c>(同一文件系统,可恢复),<see cref="FilesMutateResult.RecoveryPath"/> 给出它的位置。
/// </summary>
public sealed record FilesMutateParams(string WorkspaceId, string Path, string Operation, string? Destination = null, bool Overwrite = false);

public sealed record FilesMutateResult(string Path, string? Destination = null, string? RecoveryPath = null);

public static class FileOperations
{
    public const string CreateFile = "createFile";
    public const string CreateDirectory = "createDirectory";
    public const string Rename = "rename";
    public const string Move = "move";
    public const string Copy = "copy";
    public const string Delete = "delete";
}

// ───────────────────────── 搜索 ─────────────────────────

/// <summary>文件名建议。<see cref="Query"/> 为空返回按深度排的前几项。</summary>
public sealed record FilesSuggestParams(string WorkspaceId, string Query, int Limit = 0);

/// <summary>一条建议:<see cref="Path"/> 是项目相对、<c>/</c> 分隔,会原样写进草稿发给模型。</summary>
public sealed record FileSuggestionDto(string Path, string Name);

/// <param name="Truncated">遍历被量或时间截断了(结果可能不全)。</param>
public sealed record FilesSuggestResult(FileSuggestionDto[] Suggestions, bool Truncated);

/// <summary>
/// 内容搜索。<see cref="Pattern"/> 默认是字面串;<see cref="Regex"/> 为真时是正则(用不回溯的引擎,拒绝反向引用 / 环视这类不支持的语法,<c>files.invalidPattern</c>)。
/// 跳过忽略表里的目录、二进制文件和超过 1 MiB 的文件。<see cref="Path"/> 限定在一个子目录或单个文件;<see cref="Glob"/> 按文件名过滤(<c>*.cs</c>、<c>src/**/*.ts</c>)。
/// </summary>
public sealed record FilesSearchParams(
    string WorkspaceId,
    string Pattern,
    string Path = "",
    string? Glob = null,
    bool Regex = false,
    bool CaseSensitive = false,
    int Limit = 0);

/// <param name="Line">从 1 起。</param>
/// <param name="Column">匹配起点的列(从 1 起,按 UTF-16 码元)。</param>
/// <param name="Text">这一行的内容(超长被截断到 500 字符)。</param>
public sealed record FileMatchDto(string Path, int Line, int Column, string Text);

/// <param name="Truncated">命中数达到 <c>Limit</c>,或遍历被量 / 时间截断。</param>
public sealed record FilesSearchResult(FileMatchDto[] Matches, int FilesSearched, bool Truncated);

// ───────────────────────── markdown 资源 ─────────────────────────

/// <summary>资源种类:<c>command</c>(斜杠命令)/ <c>agent</c>(子代理)/ <c>mode</c>(会话模式)。</summary>
public static class ResourceKinds
{
    public const string Command = "command";
    public const string Agent = "agent";
    public const string Mode = "mode";
}

/// <summary>资源作用域:<c>global</c>(设备级,在数据根里)/ <c>project</c>(项目级,在项目根下 <c>.next-cowork/</c>)。</summary>
public static class ResourceScopes
{
    public const string Global = "global";
    public const string Project = "project";
}

/// <param name="WorkspaceId"><c>Scope = project</c> 时必填。</param>
public sealed record FilesResourceRefParams(string Kind, string Scope, string Name, string? WorkspaceId = null);

/// <summary>frontmatter 的一项:字符串值(<see cref="Value"/>)或列表值(<see cref="Items"/>)。保序(稳定的键序让 git diff 干净),键原样往返,包括本应用不认识的键。</summary>
public sealed record FrontmatterEntryDto(string Key, string? Value = null, string[]? Items = null);

/// <param name="Frontmatter">前置块里的键值。</param>
/// <param name="Body">正文。</param>
/// <param name="Skipped">解析器没读懂、被跳过的行(只给诊断)。</param>
/// <param name="Revision">不透明的版本串(修改时间刻度 + 大小),保存带回它。</param>
public sealed record FilesResourceDto(string Kind, string Scope, string Name, FrontmatterEntryDto[] Frontmatter, string Body, string[] Skipped, string Revision);

/// <param name="Revision">覆盖时带上读到的版本;为空 = 新建。</param>
public sealed record FilesResourceSaveParams(string Kind, string Scope, string Name, FrontmatterEntryDto[] Frontmatter, string Body, string? WorkspaceId = null, string? Revision = null);

/// <summary>文件方法的载荷形状(与其它领域同属一个 <c>PayloadShapes</c>)。</summary>
[GenerateShapeFor<FilesListParams>]
[GenerateShapeFor<FilesListResult>]
[GenerateShapeFor<FilesReadParams>]
[GenerateShapeFor<FileContentDto>]
[GenerateShapeFor<FilesReadRangeParams>]
[GenerateShapeFor<FilesRangeResult>]
[GenerateShapeFor<FilesWriteParams>]
[GenerateShapeFor<FilesWriteResult>]
[GenerateShapeFor<FilesMutateParams>]
[GenerateShapeFor<FilesMutateResult>]
[GenerateShapeFor<FilesSuggestParams>]
[GenerateShapeFor<FilesSuggestResult>]
[GenerateShapeFor<FilesSearchParams>]
[GenerateShapeFor<FilesSearchResult>]
[GenerateShapeFor<FilesResourceRefParams>]
[GenerateShapeFor<FilesResourceDto>]
[GenerateShapeFor<FilesResourceSaveParams>]
[GenerateShapeFor<FilesErrorDto>]
public sealed partial class PayloadShapes;
