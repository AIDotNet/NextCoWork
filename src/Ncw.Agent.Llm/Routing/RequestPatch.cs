using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ncw.Agent.Llm.Json;

namespace Ncw.Agent.Llm.Routing;

public enum PatchOp
{
    Add,
    Replace,
    Remove,
}

/// <summary>一条用户请求补丁(JSON Patch 的子集:add / replace / remove,路径是 JSON Pointer)。</summary>
public sealed record PatchRule(PatchOp Op, string Path, JsonElement? Value = null);

public sealed class RequestPatchException(string message) : Exception(message);

/// <summary>
/// 用户请求补丁(01-llm §5):只能改白名单里的常见厂商扩展参数,<b>禁止改 model / messages / tools / stream / 鉴权 / 传输</b>。
/// 整体原子:失败时原请求体不变。保存时校验和发送时应用走同一套规则,两边的安全规则不会漂移。
/// </summary>
public static partial class RequestPatches
{
    private static readonly HashSet<string> AllowedRoots =
    [
        "cache_control", "chat_template_kwargs", "enable_thinking", "extra_body", "frequency_penalty", "include", "logprobs",
        "max_completion_tokens", "max_output_tokens", "max_tokens", "metadata", "output_config", "parallel_tool_calls",
        "presence_penalty", "reasoning", "reasoning_effort", "reasoning_split", "response_format", "seed", "service_tier",
        "stop", "stop_sequences", "store", "system", "temperature", "thinking", "thinking_budget", "thinking_mode",
        "tool_choice", "top_k", "top_logprobs", "top_p", "user", "verbosity",
    ];

    private static readonly HashSet<string> ForbiddenSegments =
    [
        "__proto__", "prototype", "constructor", "model", "messages", "stream", "tools", "authorization", "authentication", "auth",
        "api_key", "apikey", "api-key", "x-api-key", "x_api_key", "credential", "credentials", "headers", "header", "http_headers",
        "url", "base_url", "baseurl", "access_token",
    ];

    [GeneratedRegex("~(?![01])")]
    private static partial Regex BadEscape();

    [GeneratedRegex("^(?:0|[1-9][0-9]*)$")]
    private static partial Regex ArrayIndex();

    private static string[] DecodePointer(string path)
    {
        if (path.Length == 0 || path[0] != '/')
        {
            throw new RequestPatchException($"Request patch path must be a non-empty JSON Pointer: {path}");
        }

        return path[1..].Split('/').Select(raw =>
        {
            if (BadEscape().IsMatch(raw))
            {
                throw new RequestPatchException($"Request patch path has an invalid escape: {path}");
            }

            return raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
        }).ToArray();
    }

    private static void AssertAllowed(string path, string[] parts)
    {
        if (parts[0].Length == 0 || !AllowedRoots.Contains(parts[0]))
        {
            throw new RequestPatchException($"Request patch path is not on the parameter allow list: {path}");
        }

        if (parts.FirstOrDefault(p => ForbiddenSegments.Contains(p.ToLowerInvariant())) is { } forbidden)
        {
            throw new RequestPatchException($"Request patch path may not modify '{forbidden}': {path}");
        }

        if (parts[^1].Length == 0)
        {
            throw new RequestPatchException($"Request patch path may not end with an empty segment: {path}");
        }
    }

    /// <summary>值里出现禁止的键也不行:整体替换一个允许的容器(比如 /metadata)不能绕过同一套段策略。</summary>
    private static bool IsAllowedValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().All(p => !ForbiddenSegments.Contains(p.Name.ToLowerInvariant()) && IsAllowedValue(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(IsAllowedValue),
        JsonValueKind.Undefined => false,
        _ => true,
    };

    /// <summary>保存时校验(无副作用);通过返回 null,否则返回第一条问题的描述。</summary>
    public static string? Validate(IReadOnlyList<PatchRule> patches)
    {
        try
        {
            foreach (var patch in patches)
            {
                Normalize(patch);
            }

            return null;
        }
        catch (RequestPatchException e)
        {
            return e.Message;
        }
    }

    private static void Normalize(PatchRule patch)
    {
        AssertAllowed(patch.Path, DecodePointer(patch.Path));
        if (patch.Op != PatchOp.Remove)
        {
            if (patch.Value is null)
            {
                throw new RequestPatchException($"The {patch.Op} patch for {patch.Path} is missing a value.");
            }

            if (!IsAllowedValue(patch.Value.Value))
            {
                throw new RequestPatchException($"The value of the patch for {patch.Path} is not a valid JSON value.");
            }
        }
    }

    private static int Index(string segment, int length, bool allowEnd)
    {
        if (!ArrayIndex().IsMatch(segment) || !int.TryParse(segment, out var index))
        {
            throw new RequestPatchException($"Request patch array index is invalid: {segment}");
        }

        if (index < 0 || index > (allowEnd ? length : length - 1))
        {
            throw new RequestPatchException($"Request patch array index is out of range: {segment}");
        }

        return index;
    }

    /// <summary>原子地应用补丁:先克隆,任何一条失败都不影响原请求体。</summary>
    public static JsonObject Apply(JsonObject body, IReadOnlyList<PatchRule>? patches)
    {
        if (patches is null || patches.Count == 0)
        {
            return body;
        }

        foreach (var patch in patches)
        {
            Normalize(patch);
        }

        var root = Jsn.Clone(body);
        foreach (var patch in patches)
        {
            ApplyOne(root, patch);
        }

        return root;
    }

    private static void ApplyOne(JsonObject root, PatchRule patch)
    {
        var parts = DecodePointer(patch.Path);
        AssertAllowed(patch.Path, parts);
        var key = parts[^1];

        JsonNode? cursor = root;
        foreach (var part in parts[..^1])
        {
            cursor = cursor switch
            {
                JsonArray array => array[Index(part, array.Count, false)],
                JsonObject obj when obj.ContainsKey(part) => obj[part],
                _ => throw new RequestPatchException($"Request patch parent path does not exist: {patch.Path}"),
            };
            if (cursor is not (JsonObject or JsonArray))
            {
                throw new RequestPatchException($"Request patch parent path is not an object or array: {patch.Path}");
            }
        }

        var value = patch.Value is { } v ? Jsn.ToNode(v) : null;
        switch (cursor)
        {
            case JsonArray array:
                if (patch.Op == PatchOp.Add)
                {
                    if (key == "-")
                    {
                        array.Push(value);
                    }
                    else
                    {
                        array.Insert(Index(key, array.Count, true), value);
                    }

                    return;
                }

                var at = Index(key, array.Count, false);
                if (patch.Op == PatchOp.Remove)
                {
                    array.RemoveAt(at);
                }
                else
                {
                    array[at] = value;
                }

                return;

            case JsonObject obj:
                if (patch.Op == PatchOp.Add)
                {
                    obj[key] = value;
                    return;
                }

                if (!obj.ContainsKey(key))
                {
                    throw new RequestPatchException($"Request patch target does not exist: {patch.Path}");
                }

                if (patch.Op == PatchOp.Remove)
                {
                    obj.Remove(key);
                }
                else
                {
                    obj[key] = value;
                }

                return;
        }
    }
}
