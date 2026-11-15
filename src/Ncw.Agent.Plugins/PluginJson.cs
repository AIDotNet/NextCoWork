using System.Text.Json.Serialization;

namespace Ncw.Agent.Plugins;

/// <summary>源生成的 JSON 上下文(只用来写字符串字面量;不用反射序列化,AGENTS.md §2)。</summary>
[JsonSerializable(typeof(string))]
internal sealed partial class PluginJson : JsonSerializerContext;
