using System.Buffers;
using System.Text.Json;
using Ncw.Agent.Environments;
using Ncw.Agent.Kernel.Tools;
using Ncw.Agent.Llm.Json;
using Ncw.Agent.Tools.Schema;

namespace Ncw.Agent.Tools;

/// <summary>给模型看的失败:消息原样进 <c>tool_result</c>(带「下一步该做什么」)。工具里 <c>throw</c> 它比层层返回 <see cref="ToolResult"/> 省事。</summary>
public sealed class ToolFailureException(string message, JsonElement? details = null) : Exception(message)
{
    public JsonElement? Details { get; } = details;
}

/// <summary>内置工具的公共骨架:参数校验 + 宽松转换、环境错误的统一翻译、「没有执行环境」的说明。</summary>
public abstract class BuiltinTool : IAgentTool
{
    protected BuiltinTool(string name, string description, JsonElement schema, ToolTraits traits, string promptSnippet, IReadOnlyList<string> guidelines)
    {
        Info = new ToolSpec(name, description, schema, traits, promptSnippet, guidelines);
    }

    public ToolSpec Info { get; }

    public virtual bool IsEnabled(ToolContext ctx) => true;

    public ToolArgumentCheck PrepareArguments(JsonElement input)
    {
        var result = JsonSchemaValidator.Validate(Info.InputSchema, input);
        return result.IsValid ? ToolArgumentCheck.Valid(result.Value) : ToolArgumentCheck.Invalid($"Invalid arguments — {result.Error}");
    }

    public async ValueTask<ToolResult> ExecuteAsync(ToolInvocation call, ToolContext ctx, CancellationToken ct)
    {
        // 直接调用(没经过 Loop 的预检)也要有校验:再过一遍是幂等的
        var check = PrepareArguments(call.Input);
        if (!check.IsValid)
        {
            return ToolResult.Error(check.Error!);
        }

        if (ctx is not ExecutionToolContext execution)
        {
            return ToolResult.Error("This tool needs an execution environment, but this session did not provide one.");
        }

        try
        {
            return await RunAsync(check.Input, execution, ct).ConfigureAwait(false);
        }
        catch (ToolFailureException e)
        {
            return ToolResult.Error(e.Message, e.Details);
        }
        catch (EnvironmentException e)
        {
            return ToolResult.Error(Describe(e, execution.Runtime));
        }
    }

    protected abstract ValueTask<ToolResult> RunAsync(JsonElement args, ExecutionToolContext ctx, CancellationToken ct);

    protected static ToolFailureException Fail(string message, JsonElement? details = null) => new(message, details);

    /// <summary>环境层的结构化错误 → 给模型看的一句话。只有「路径不存在」这类各工具自己有更具体说法的情形,会在工具里先 catch。</summary>
    internal static string Describe(EnvironmentException e, ToolRuntime runtime)
    {
        var where = $"{runtime.Environment.DisplayName} ({runtime.EnvironmentId})";
        var detail = e.Detail is { Length: > 0 } d ? $": {d}" : "";
        return e.Code switch
        {
            EnvironmentErrorCode.NotFound => $"Path does not exist{detail}",
            EnvironmentErrorCode.NotDirectory => $"A path component is not a directory{detail}",
            EnvironmentErrorCode.IsDirectory => $"The path is a directory{detail}",
            EnvironmentErrorCode.PermissionDenied => $"Permission denied{detail}",
            EnvironmentErrorCode.TooLarge => $"The file is too large for this tool{detail}",
            EnvironmentErrorCode.InvalidPath => $"Invalid path{detail}",
            EnvironmentErrorCode.AlreadyExists => $"The target already exists{detail}",
            EnvironmentErrorCode.Disconnected or EnvironmentErrorCode.ConnectionFailed or EnvironmentErrorCode.StaleGeneration =>
                $"The execution environment {where} is not reachable (connection lost). Nothing was changed by this call unless stated otherwise; retry once it is reconnected.",
            EnvironmentErrorCode.ProcessStartFailed => $"Could not start the command in {where}{detail}",
            EnvironmentErrorCode.ResultUnknown =>
                $"The connection to {where} was lost while writing; the result is UNKNOWN. Read the file again to see what was actually written before you retry.",
            _ => $"File system error on {where} ({e.Code}){detail}",
        };
    }

    // ── 参数读取 ──

    protected static string RequiredString(JsonElement args, string name) => args.Str(name) ?? "";

    protected static int? OptionalInt(JsonElement args, string name) => args.Num(name) is { } n ? (int)Math.Clamp(n, int.MinValue, int.MaxValue) : null;

    // ── Details:只给界面,永远不发给模型。每份都带执行环境标识(设备 + 环境,AGENTS.md §5) ──

    public static JsonElement Details(ToolRuntime runtime, string kind, Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", kind);
            w.WriteStartObject("environment");
            w.WriteString("deviceId", runtime.EnvironmentId.DeviceId);
            w.WriteString("environment", runtime.EnvironmentId.Environment);
            w.WriteString("name", runtime.Environment.DisplayName);
            w.WriteString("type", runtime.Environment.Kind == EnvironmentKind.Local ? "local" : "ssh");
            w.WriteEndObject();
            body(w);
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }
}
