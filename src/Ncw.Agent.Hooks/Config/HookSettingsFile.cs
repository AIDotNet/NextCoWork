using System.Text;
using System.Text.Json;
using Ncw.Agent.Extensions.Permissions;

namespace Ncw.Agent.Hooks;

/// <summary>读一份设置文件里的 <c>hooks</c> 段的结果。</summary>
/// <param name="Hooks">读得懂的钩子(按 <see cref="HookEvents.All"/> 的顺序);没有 id 的条目 id 是空串(见 <see cref="HookSettingsFile.AssignIds"/>)。</param>
/// <param name="RawCount">文件里 hooks 段一共写了几条(不做任何校验);和 <c>Hooks.Count</c> 的差值就是被丢掉的条数(诊断红条的来源)。</param>
/// <param name="Unreadable">整份文件不是合法 JSON(或顶层不是对象):读为空,但<b>拒绝覆盖</b>。</param>
public sealed record HookFileContents(IReadOnlyList<HookDefinition> Hooks, int RawCount, bool Unreadable)
{
    public static HookFileContents Empty { get; } = new([], 0, false);
}

/// <summary>
/// 设置文件里 <c>hooks</c> 段 ⇄ 列表(2.x <c>kernel/hook/load.ts</c> + <c>shared/domain/local-settings.ts</c> 的 hooks 部分;纯函数,零 IO)。
/// 文件仍是 2.x 的那一份:项目级 <c>&lt;项目&gt;/.next-cowork/settings.local.json</c>,全局级 <c>&lt;profile 根&gt;/settings.json</c>;
/// 本类写的时候<b>原样保留 hooks 以外的所有键</b>(权限规则段由 <see cref="ProjectRuleFile"/> 管,两边互不抹掉)。
/// <list type="bullet">
/// <item>读的时候宽容:一条读不懂的钩子丢掉,<b>不是</b>让整个文件作废;兼容 Claude Code 的嵌套写法(<c>{ matcher, hooks: [...] }</c>)并拍平,写出去一律是扁平形状;</item>
/// <item><c>timeout</c> 是<b>秒</b>(给人手写的,<c>30</c> 比 <c>30000</c> 好读),内存里统一毫秒;<c>enabled</c> 缺省 true,开启时把键去掉;command 型不写 <c>type</c>(缺省就是它);</item>
/// <item>3.0 新增可选键 <c>env</c>(command 型的环境变量,值不上线);2.x 读到它只是一个不认识的键。</item>
/// </list>
/// </summary>
public static class HookSettingsFile
{
    public const int Version = 1;

    public static HookFileContents Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return HookFileContents.Empty;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, HookJson.Document);
        }
        catch (JsonException)
        {
            return new HookFileContents([], 0, true);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new HookFileContents([], 0, true);
            }

            return doc.RootElement.TryGetProperty("hooks", out var hooks) ? ParseSection(hooks) : HookFileContents.Empty;
        }
    }

    private static HookFileContents ParseSection(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object)
        {
            return HookFileContents.Empty;
        }

        var raw = 0;
        var byEvent = new Dictionary<string, List<HookDefinition>>(StringComparer.Ordinal);
        foreach (var property in section.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            raw += property.Value.GetArrayLength();
            if (!HookEvents.IsValid(property.Name))
            {
                continue;
            }

            var list = new List<HookDefinition>();
            foreach (var item in property.Value.EnumerateArray())
            {
                foreach (var flat in Flatten(item))
                {
                    if (Entry(flat, property.Name) is { } hook)
                    {
                        list.Add(hook);
                    }

                    if (list.Count >= HookLimits.MaxPerEvent)
                    {
                        break;
                    }
                }

                if (list.Count >= HookLimits.MaxPerEvent)
                {
                    break;
                }
            }

            if (list.Count > 0)
            {
                byEvent[property.Name] = list;
            }
        }

        return new HookFileContents([.. HookEvents.All.SelectMany(e => byEvent.GetValueOrDefault(e) ?? [])], raw, false);
    }

    /// <summary>CC 的 <c>{ matcher, hooks: [...] }</c> 拍平成若干条(内层带上外层的 matcher);本项目自己的形状原样放行。</summary>
    private static IEnumerable<(JsonElement Item, string? Matcher)> Flatten(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        if (item.TryGetProperty("hooks", out var inner) && inner.ValueKind == JsonValueKind.Array)
        {
            var outer = item.TryGetProperty("matcher", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            foreach (var i in inner.EnumerateArray())
            {
                if (i.ValueKind == JsonValueKind.Object)
                {
                    yield return (i, outer);
                }
            }

            yield break;
        }

        yield return (item, null);
    }

    private static HookDefinition? Entry((JsonElement Item, string? Matcher) flat, string hookEvent)
    {
        var item = flat.Item;
        string? Str(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        // type 缺省 command(老文件里一条都没写过它);写了个不认识的词就丢掉这一条——一个 type: "webhook" 的条目按 command 读会静默地跑一条空命令。
        var rawType = Str("type");
        if (item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        HookKind kind;
        switch (rawType)
        {
            case null or "command":
                kind = HookKind.Command;
                break;
            case "prompt":
                kind = HookKind.Prompt;
                break;
            default:
                return null;
        }

        var matcher = (Str("matcher") ?? flat.Matcher)?.Trim() ?? "";
        // 读不懂的 matcher 直接丢掉这一条:留着它只会在某天以「我明明写了」的形式变成一次没拦住,而那正是安全类钩子最不该有的失败方式。
        if (matcher.Length > 0 && !PermissionRules.IsValid(matcher))
        {
            return null;
        }

        double seconds = HookLimits.DefaultTimeout(hookEvent, kind) / 1000.0;
        if (item.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var parsed) && parsed > 0 && double.IsFinite(parsed))
        {
            seconds = parsed;
        }

        var timeoutMs = (int)Math.Min(Math.Round(seconds * 1000), HookLimits.MaxTimeoutMs);
        var description = Str("description")?.Trim();
        var hook = new HookDefinition
        {
            Id = Str("id") ?? "",
            Event = hookEvent,
            Kind = kind,
            Matcher = matcher.Length == 0 ? null : matcher,
            Enabled = !(item.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False),
            TimeoutMs = timeoutMs,
            Description = string.IsNullOrEmpty(description) ? null : description,
        };
        if (kind == HookKind.Prompt)
        {
            var prompt = Str("prompt")?.Trim() ?? "";
            if (prompt.Length == 0 || prompt.Length > HookLimits.PromptMax)
            {
                return null;
            }

            var model = Str("model")?.Trim() ?? "";
            var provider = Str("modelProviderId")?.Trim() ?? "";
            return hook with
            {
                Prompt = prompt,
                Model = model.Length == 0 ? null : model,
                // 供应商只在有别名时才留:单独一个供应商 id 配不出任何一条绑定。
                ModelProviderId = model.Length == 0 || provider.Length == 0 ? null : provider,
            };
        }

        var command = Str("command")?.Trim() ?? "";
        if (command.Length == 0 || command.Length > HookLimits.CommandMax)
        {
            return null;
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("env", out var envElement) && envElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in envElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } value)
                {
                    env[p.Name] = value;
                }
            }
        }

        // 不合法的环境变量(名字 / 个数 / 保留前缀)整条钩子读不懂:带着一半环境变量去跑一条脚本,比不跑更危险。
        return HookValidation.ValidateEnv(env) is not null ? null : hook with { Command = command, Env = env };
    }

    // ───────────────────────── 写 ─────────────────────────

    /// <summary>
    /// 写出整份文件:hooks 以外的键原样带过去(顺序也不动);<c>hooks</c> 段重写为扁平形状;<c>version</c> 缺失时补上。
    /// <paramref name="existingText"/> 读不懂时调用方应当拒绝覆盖(<see cref="HookFileContents.Unreadable"/>),这里当作空对象。
    /// </summary>
    public static string Render(string? existingText, IReadOnlyList<HookDefinition> hooks)
    {
        JsonDocument? existing = null;
        if (!string.IsNullOrWhiteSpace(existingText))
        {
            try
            {
                existing = JsonDocument.Parse(existingText, HookJson.Document);
            }
            catch (JsonException)
            {
            }
        }

        using (existing)
        {
            using var stream = new MemoryStream();
            using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                var wroteHooks = false;
                var wroteVersion = false;
                if (existing?.RootElement is { ValueKind: JsonValueKind.Object } root)
                {
                    foreach (var p in root.EnumerateObject())
                    {
                        if (p.NameEquals("hooks"))
                        {
                            if (!wroteHooks)
                            {
                                WriteHooks(w, hooks);
                                wroteHooks = true;
                            }
                        }
                        else
                        {
                            wroteVersion |= p.NameEquals("version");
                            p.WriteTo(w);
                        }
                    }
                }

                if (!wroteVersion)
                {
                    w.WriteNumber("version", Version);
                }

                if (!wroteHooks)
                {
                    WriteHooks(w, hooks);
                }

                w.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
        }
    }

    private static void WriteHooks(Utf8JsonWriter w, IReadOnlyList<HookDefinition> hooks)
    {
        w.WritePropertyName("hooks");
        w.WriteStartObject();
        foreach (var hookEvent in HookEvents.All)
        {
            var entries = hooks.Where(h => h.Event == hookEvent).ToList();
            if (entries.Count == 0)
            {
                continue;
            }

            w.WriteStartArray(hookEvent);
            foreach (var hook in entries)
            {
                WriteEntry(w, hook);
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter w, HookDefinition hook)
    {
        w.WriteStartObject();
        w.WriteString("id", hook.Id);
        if (!string.IsNullOrEmpty(hook.Matcher))
        {
            w.WriteString("matcher", hook.Matcher);
        }

        // command 型不写 type:缺省值就是它,写上去会让一次无关的改动把老文件重排成另一种写法。
        if (hook.Kind == HookKind.Prompt)
        {
            w.WriteString("type", "prompt");
            w.WriteString("prompt", hook.Prompt);
            if (!string.IsNullOrEmpty(hook.Model))
            {
                w.WriteString("model", hook.Model);
            }

            if (!string.IsNullOrEmpty(hook.Model) && !string.IsNullOrEmpty(hook.ModelProviderId))
            {
                w.WriteString("modelProviderId", hook.ModelProviderId);
            }
        }
        else
        {
            w.WriteString("command", hook.Command);
        }

        if (!hook.Enabled)
        {
            w.WriteBoolean("enabled", false);
        }

        w.WriteNumber("timeout", hook.TimeoutMs / 1000.0);
        if (!string.IsNullOrEmpty(hook.Description))
        {
            w.WriteString("description", hook.Description);
        }

        if (hook.Kind == HookKind.Command && hook.Env.Count > 0)
        {
            w.WriteStartObject("env");
            foreach (var (name, value) in hook.Env.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.WriteString(name, value);
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
    }

    // ───────────────────────── 增删改(纯函数) ─────────────────────────

    /// <summary>给没有 id 的条目补一个。手写的条目不可能自带 ULID,而开关和删除都需要稳定的键。</summary>
    public static IReadOnlyList<HookDefinition> AssignIds(IReadOnlyList<HookDefinition> hooks, Func<string> mint) =>
        [.. hooks.Select(h => h.Id.Length == 0 ? h with { Id = mint() } : h)];

    public static bool HasMissingIds(IReadOnlyList<HookDefinition> hooks) => hooks.Any(h => h.Id.Length == 0);

    /// <summary>按 id upsert。先把这条 id 从所有事件下摘掉(用户可能把它从 PreToolUse 改成了 PostToolUse,只在目标事件里 upsert 会让它同时留在两个事件下)。</summary>
    public static IReadOnlyList<HookDefinition> Upsert(IReadOnlyList<HookDefinition> hooks, HookDefinition hook) =>
        [.. hooks.Where(h => h.Id != hook.Id), hook];

    public static IReadOnlyList<HookDefinition> Remove(IReadOnlyList<HookDefinition> hooks, string id) => [.. hooks.Where(h => h.Id != id)];

    public static IReadOnlyList<HookDefinition> SetEnabled(IReadOnlyList<HookDefinition> hooks, string id, bool enabled) =>
        [.. hooks.Select(h => h.Id == id ? h with { Enabled = enabled } : h)];
}
