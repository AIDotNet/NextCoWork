using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Ncw.Build.Bundle;
using Ncw.Build.Release;

namespace Ncw.Build.Core;

/// <summary>源生成的 JSON 上下文(不用反射式序列化,AGENTS.md §2)。</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(BundleManifest))]
[JsonSerializable(typeof(ReleaseManifest))]
internal sealed partial class BuildJson : JsonSerializerContext;

internal static class JsonOut
{
    public static string Serialize<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.Serialize(value, info) + "\n";

    public static T Deserialize<T>(string json, JsonTypeInfo<T> info, string source) =>
        JsonSerializer.Deserialize(json, info) ?? throw new BuildException($"{source} is empty or invalid.");
}
