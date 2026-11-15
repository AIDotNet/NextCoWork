using System.Text;
using System.Text.Json;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm;

namespace Ncw.Agent.Plugins;

/// <summary>
/// 一个插件贡献的 Agent 工具(<see cref="IAgentTool"/>)。进的是和内置、MCP 工具<b>同一张表</b>(<see cref="Ncw.Agent.Kernel.Session.ToolRegistry"/>,来源 <c>plugin:&lt;id&gt;</c>,内部 id <c>plugin__&lt;publisher&gt;_&lt;name&gt;__&lt;tool&gt;</c>);
/// 权限完全走权限扩展(<c>InternalId</c> 就是规则里的工具名),<b>插件只能收紧不能放行</b>:
/// <list type="bullet">
/// <item><see cref="ToolTraits"/>:<c>ReadOnly</c> 只有在插件<b>声明了只读且清单里没有任何写类能力</b>(<c>workspace.write</c> / <c>process</c> / <c>scm.write</c>)时才算数;<c>Destructive</c> = 声明的 或 能起进程;<c>NeedsNetwork</c> = 声明的 或 有 <c>net</c> 能力——自报的特征只会让审批更严,不会更松。</item>
/// <item>描述加 <c>[Plugin:…]</c> 前缀并消毒(进系统提示词,第三方来源 = 不可信输入);schema 原样下发,宿主不解释(参数校验交给插件)。</item>
/// <item><c>Details</c> 带执行环境标识(设备 + 环境 + 名字 + 类型,AGENTS.md §5)和插件 / 工具名;卡片只走这条 UI 轨。</item>
/// <item>中止(<see cref="OperationCanceledException"/> 且令牌已触发)<b>永远原样传播</b>,不伪装成工具失败;插件 throw / 超预算 / 不在线 = 工具错误(进转录,模型换条路),Run 不挂。</item>
/// </list>
/// </summary>
public sealed class PluginTool : IAgentTool
{
    public const int MaxResultChars = 60_000;

    private readonly PluginManager _manager;
    private readonly string _pluginId;
    private readonly string _toolName;

    internal PluginTool(PluginManager manager, PluginManifest manifest, PluginToolDeclaration declaration)
    {
        _manager = manager;
        _pluginId = manifest.Id;
        _toolName = declaration.Name;
        var mutating = manifest.Permissions.Concat(manifest.OptionalPermissions).Any(PluginPermissions.IsMutating);
        var network = manifest.Permissions.Concat(manifest.OptionalPermissions).Contains(PluginPermissions.Net);
        Info = new ToolSpec(
            PluginToolNames.InternalId(manifest.Id, declaration.Name),
            $"[Plugin:{manifest.DisplayName}]\n{(declaration.Description.Length > 0 ? declaration.Description : "(this plugin provided no description for the tool)")}",
            ParseSchema(declaration.InputSchemaJson),
            new ToolTraits(
                ReadOnly: declaration.ReadOnly && !mutating,
                Destructive: declaration.Destructive || mutating, // 写类能力(含 process)= 保守地按破坏性,不看插件自报的
                NeedsNetwork: declaration.NeedsNetwork || network));
        Source = PluginToolNames.Source(manifest.Id);
    }

    public ToolSpec Info { get; }

    /// <summary>注册表里的来源名(<c>plugin:&lt;id&gt;</c>)。</summary>
    public string Source { get; }

    public string PluginId => _pluginId;

    public string ToolName => _toolName;

    public bool IsEnabled(ToolContext ctx) => _manager.IsToolAvailable(_pluginId, _toolName);

    public ToolArgumentCheck PrepareArguments(JsonElement input) => input.ValueKind switch
    {
        JsonValueKind.Object => ToolArgumentCheck.Valid(input),
        JsonValueKind.Undefined or JsonValueKind.Null => ToolArgumentCheck.Valid(JsonDocument.Parse("{}").RootElement.Clone()),
        _ => ToolArgumentCheck.Invalid("Invalid arguments: the input for a plugin tool must be a JSON object."),
    };

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        var check = PrepareArguments(call.Input);
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!, _manager.ToolDetails(_pluginId, _toolName, ctx, null));
        }

        return await _manager.InvokeToolAsync(_pluginId, _toolName, check.Input, call.CallId, ctx, ct).ConfigureAwait(false);
    }

    private static JsonElement ParseSchema(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
            {
                return doc.RootElement.Clone();
            }
        }
        catch (JsonException)
        {
        }

        // 认不出的 schema 退化成一个空对象:上游拒绝没有 type 的 schema
        return JsonDocument.Parse("{\"type\":\"object\",\"properties\":{}}").RootElement.Clone();
    }

    /// <summary>插件返回的结果 → 工具输出。认不出的形状退化成「把它当文本」,而不是报错;<c>card</c> 只进 <c>Details</c>(UI 轨),经 <see cref="PluginCards"/> 消毒,模型永远看不到。</summary>
    internal static (string Text, bool IsError, JsonElement? Card) ReadResult(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        switch (root.ValueKind)
        {
            case JsonValueKind.String:
                return (root.GetString()!, false, null);
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return ("", false, null);
            case JsonValueKind.Object:
                JsonElement? card = root.TryGetProperty("card", out var c) && PluginCards.Sanitize(c) is { } sanitized ? sanitized : null;
                var isError = root.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    var parts = new List<string>();
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(part.GetString()!);
                        }
                        else if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } t)
                        {
                            parts.Add(t);
                        }
                    }

                    return (string.Join('\n', parts), isError, card);
                }

                // 有 card 但 content 不是数组:别把整个对象塞进文本(那会把 card 也塞进模型可见的 content)
                return card is not null ? ("", isError, card) : (root.GetRawText(), isError, null);
            default:
                return (root.GetRawText(), false, null);
        }
    }

    internal static ToolResult Finish(string text, bool isError, JsonElement details)
    {
        var truncated = text.Length > MaxResultChars;
        long? original = null;
        if (truncated)
        {
            original = Encoding.UTF8.GetByteCount(text);
            var keep = MaxResultChars;
            if (char.IsHighSurrogate(text[keep - 1]))
            {
                keep--;
            }

            text = text[..keep] + $"\n\n[Output truncated: {text.Length - keep} more characters were omitted.]";
        }

        return new ToolResult([new TextPart(text)], details, IsError: isError, Truncated: truncated, OriginalBytes: original);
    }
}
