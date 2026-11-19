using PolyType;

namespace Ncw.Protocol.Methods;

// 供应商自动发现与导入(docs/design/07 §3「供应商自动发现与导入」、09 §8.4)。后加的能力 `providers.discovery`:
// 设备在自己那台机器上找其他客户端(Claude Code、Codex、cc-switch、opencode、Gemini CLI、Qwen Code、Crush、Continue、Aider)、Shell 配置、
// 环境变量里已经配好的供应商,以及本地服务(Ollama、LM Studio),控制端只看到摘要,
// 导入也在设备上完成。★ 密钥明文从不出现在任何 DTO 里:候选只带后四位,导入只传候选 id(设备重新扫一遍、自己存密钥)。

/// <summary>扫到的一个候选供应商。</summary>
/// <param name="Id">候选 id(同一份配置每次扫描都一样;导入时传回)。</param>
/// <param name="Source">来源(<see cref="DiscoverySources"/>:<c>claude-code</c>、<c>codex</c>、<c>shell</c>、<c>local</c>……);开放的字符串,以后加来源不改线上格式。</param>
/// <param name="Origin">给人看的出处:文件路径(家目录写成 <c>~</c>)或环境变量名;cc-switch 的条目带它自己的配置名。</param>
/// <param name="AlsoSeenIn">同一地址 + 同一把 key 在别的来源里也有(那些来源的 <see cref="Source"/>)。</param>
/// <param name="SuggestedId">导入时建议的供应商 id(冲突时设备会加后缀)。</param>
/// <param name="Protocol"><c>anthropic</c> / <c>openai-chat</c> / <c>openai-responses</c>。</param>
/// <param name="KeyLast4">密钥后四位;没有 key(例如本地 Ollama)为 null。</param>
/// <param name="Models">来源里提到的模型(导入时建成别名);没有为空。</param>
/// <param name="Status"><c>new</c> / <c>imported</c>(已有同地址同 key 的供应商)/ <c>conflict</c>(地址相同但 key 不同,或建议的 id 被占)。</param>
/// <param name="ExistingProviderId"><c>imported</c> / <c>conflict</c> 时对上的那个现有供应商。</param>
public sealed record DiscoveredProviderDto(
    string Id,
    string Source,
    string Origin,
    string[] AlsoSeenIn,
    string SuggestedId,
    string Name,
    string Protocol,
    string BaseUrl,
    string? KeyLast4,
    string[] Models,
    string Status,
    string? ExistingProviderId);

public sealed record DiscoveryScanResult(DiscoveredProviderDto[] Candidates);

public sealed record DiscoveryImportParams(string[] Ids);

/// <summary>一条候选的导入结局:成功给新供应商的 id,失败给稳定的 key(<see cref="DiscoveryErrorKeys"/> 或供应商的错误 key)。</summary>
public sealed record DiscoveryImportItemDto(string CandidateId, string? ProviderId, string? ErrorKey);

public sealed record DiscoveryImportResult(DiscoveryImportItemDto[] Items);

/// <summary>候选的来源。</summary>
public static class DiscoverySources
{
    public const string ClaudeCode = "claude-code";
    public const string Codex = "codex";
    public const string CcSwitch = "cc-switch";
    public const string OpenCode = "opencode";
    public const string GeminiCli = "gemini-cli";
    public const string QwenCode = "qwen-code";
    public const string Crush = "crush";
    public const string Continue = "continue";
    public const string Aider = "aider";

    /// <summary>Shell 配置文件(<c>~/.zshrc</c> 等)里 export 的变量。</summary>
    public const string Shell = "shell";

    public const string Env = "env";

    /// <summary>本地服务(Ollama、LM Studio),不要 key。</summary>
    public const string Local = "local";

    /// <summary>全部来源(新增来源只加值,线上格式不变:<see cref="DiscoveredProviderDto.Source"/> 是开放的字符串,控制端认不出就原样显示)。</summary>
    public static readonly IReadOnlyList<string> All = [ClaudeCode, Codex, CcSwitch, OpenCode, GeminiCli, QwenCode, Crush, Continue, Aider, Shell, Env, Local];
}

/// <summary>候选的状态。</summary>
public static class DiscoveryStatus
{
    public const string New = "new";
    public const string Imported = "imported";
    public const string Conflict = "conflict";
}

public static class DiscoveryErrorKeys
{
    /// <summary>扫描之后来源变了,这个候选已经不在了。</summary>
    public const string Gone = "providers.discovery.gone";

    /// <summary>已经有同地址同 key 的供应商。</summary>
    public const string AlreadyImported = "providers.discovery.alreadyImported";
}

/// <summary>供应商自动发现的方法表(能力 <see cref="Capabilities.ProviderDiscovery"/>,承载 本机 + 远程,owner)。</summary>
public static class ProviderDiscoveryMethods
{
    /// <summary>扫一遍(只读,不写任何东西)。</summary>
    public static readonly MethodDef<Unit, DiscoveryScanResult> Scan =
        new("providers.discovery.scan", MethodKind.Query, Role.Owner, Transports.Any, PayloadShapes.GetShape<Unit>(), PayloadShapes.GetShape<DiscoveryScanResult>())
        {
            Capability = Capabilities.ProviderDiscovery,
        };

    /// <summary>按候选 id 导入(设备重新扫一遍,在设备上建供应商、存密钥、建别名)。</summary>
    public static readonly MethodDef<DiscoveryImportParams, DiscoveryImportResult> Import =
        new("providers.discovery.import", MethodKind.Command, Role.Owner, Transports.Any, PayloadShapes.GetShape<DiscoveryImportParams>(), PayloadShapes.GetShape<DiscoveryImportResult>())
        {
            Capability = Capabilities.ProviderDiscovery,
        };

    public static readonly IReadOnlyList<MethodDef> All = [Scan, Import];
}

public static partial class Capabilities
{
    /// <summary>供应商自动发现:<c>providers.discovery.*</c>(owner;后加的能力,设备不报就不调)。</summary>
    public const string ProviderDiscovery = "providers.discovery";
}

[GenerateShapeFor<DiscoveredProviderDto>]
[GenerateShapeFor<DiscoveryScanResult>]
[GenerateShapeFor<DiscoveryImportParams>]
[GenerateShapeFor<DiscoveryImportItemDto>]
[GenerateShapeFor<DiscoveryImportResult>]
public sealed partial class PayloadShapes;
