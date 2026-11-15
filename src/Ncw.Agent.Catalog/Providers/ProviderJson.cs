using System.Text.Json.Serialization;
using Ncw.Agent.Llm.Auth;

namespace Ncw.Agent.Catalog;

/// <summary>源生成的类型信息:规范序列化里转义对象键、账号表里的额度快照 JSON。没有任何反射序列化路径。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(QuotaSnapshot))]
internal sealed partial class ProviderJsonContext : JsonSerializerContext;

internal static class ProviderJson
{
    public static ProviderJsonContext Context => ProviderJsonContext.Default;
}

/// <summary>JsonElement 的源生成类型信息(把 JsonNode 里的值交给 Llm 层的 <c>JsonElement</c> 字段)。</summary>
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
internal sealed partial class ModelsDevElementContext : JsonSerializerContext;
