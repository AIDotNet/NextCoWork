namespace Ncw.Core.Domain;

/// <summary>
/// 供应商地址 + 协议 → 实际请求的 URL(docs/design/07「供应商调试」)。设备发请求(<c>WireProtocols.JoinUrl</c>)和调试弹窗显示「最终地址」用的是这同一份规则:
/// 设置页必须把最终拼出的 URL 显示出来,因为这条规则不可能对所有 base 都正确。协议名是线上的三个值(<c>anthropic</c> / <c>openai-chat</c> / <c>openai-responses</c>)。
/// </summary>
public static class ProviderEndpoints
{
    public const string Anthropic = "anthropic";
    public const string OpenAiChat = "openai-chat";
    public const string OpenAiResponses = "openai-responses";

    /// <summary>三种协议,界面列出的顺序。</summary>
    public static readonly IReadOnlyList<string> Protocols = [Anthropic, OpenAiChat, OpenAiResponses];

    /// <summary>
    /// 对话请求的路径。两族协议的版本段约定是反的:Anthropic 的 base 不带 <c>/v1</c>,路径自己补;
    /// OpenAI 族的版本段是 base 的一部分(智谱 <c>/api/paas/v4</c>、火山 <c>/api/v3</c>……),路径只有 <c>/chat/completions</c>。认不出的协议为 null。
    /// </summary>
    public static string? ChatPath(string protocol) => protocol switch
    {
        Anthropic => "/v1/messages",
        OpenAiChat => "/chat/completions",
        OpenAiResponses => "/responses",
        _ => null,
    };

    /// <summary>
    /// base 与 path 拼接。去重 <c>/v1</c> 只服务 Anthropic:用户手填 <c>.../anthropic/v1</c> 时避免拼出 <c>/v1/v1/messages</c>。
    /// </summary>
    public static string JoinUrl(string baseUrl, string path)
    {
        var baseTrimmed = baseUrl.TrimEnd('/');
        var p = path.StartsWith('/') ? path : "/" + path;
        return baseTrimmed.EndsWith("/v1", StringComparison.Ordinal) && p.StartsWith("/v1/", StringComparison.Ordinal)
            ? baseTrimmed + p[3..]
            : baseTrimmed + p;
    }

    /// <summary>这个地址用这个协议对话时请求的 URL;协议认不出或地址为空为 null。</summary>
    public static string? ChatUrl(string baseUrl, string protocol) =>
        string.IsNullOrWhiteSpace(baseUrl) || ChatPath(protocol) is not { } path ? null : JoinUrl(baseUrl.Trim(), path);
}
