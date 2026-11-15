using System.Text.Json;
using Ncw.Protocol.Methods;
using PolyType;
using PolyType.Abstractions;

namespace Ncw.Agent.Gateway;

/// <summary>
/// 由方法表自动导出 OpenAPI 3.1 文档(docs/design/09 §11)。方法表是唯一来源:路径、种类(query / command)、所需角色、所需能力、承载、参数 / 结果的类型形状都来自 <see cref="MethodDef"/>;
/// 类型 schema 从 PolyType 的类型形状遍历出来(源生成,零反射,NativeAOT 安全),所以新增方法 / DTO 不需要手写任何文档。
/// 只导出网关实际暴露的方法(<see cref="GatewayExposure.Exposes"/>):本机专用、未配对专用、带二进制参数的不在其中。
/// </summary>
public static class OpenApiExporter
{
    public static string Export(IEnumerable<MethodDef> methods, string title, string version)
    {
        var components = new SortedDictionary<string, Action<Utf8JsonWriter>>(StringComparer.Ordinal);
        var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("openapi", "3.1.0");
            json.WriteStartObject("info");
            json.WriteString("title", title);
            json.WriteString("version", version);
            json.WriteString("description", "The NextCoWork agent's method table over HTTP. POST /api/<method> with the JSON form of the method's parameters; commands need an Idempotency-Key header. Subscriptions are Server-Sent Events at GET /api/topics/<topic>?cursor=.");
            json.WriteEndObject();
            json.WriteStartArray("security");
            json.WriteStartObject();
            json.WriteStartArray("bearer");
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndArray();

            json.WriteStartObject("paths");
            foreach (var method in methods.Where(GatewayExposure.Exposes).OrderBy(static m => m.Name, StringComparer.Ordinal))
            {
                json.WriteStartObject($"/api/{method.Name}");
                json.WriteStartObject("post");
                json.WriteString("operationId", method.Name);
                json.WriteString("summary", $"{method.Name} ({method.Kind.ToString().ToLowerInvariant()}, requires {method.MinRole.ToString().ToLowerInvariant()})");
                json.WriteString("x-ncw-kind", method.Kind.ToString().ToLowerInvariant());
                json.WriteString("x-ncw-minRole", method.MinRole.ToString().ToLowerInvariant());
                if (method.Capability is { } capability)
                {
                    json.WriteString("x-ncw-capability", capability);
                }

                if (method.Kind == MethodKind.Command)
                {
                    json.WriteStartArray("parameters");
                    json.WriteStartObject();
                    json.WriteString("name", "Idempotency-Key");
                    json.WriteString("in", "header");
                    json.WriteBoolean("required", true);
                    json.WriteStartObject("schema");
                    json.WriteString("type", "string");
                    json.WriteEndObject();
                    json.WriteEndObject();
                    json.WriteEndArray();
                }

                var (paramsShape, resultShape) = (method.ParamsTypeShape, method.ResultTypeShape);
                json.WriteStartObject("requestBody");
                json.WriteBoolean("required", true);
                json.WriteStartObject("content");
                json.WriteStartObject("application/json");
                json.WritePropertyName("schema");
                WriteRef(json, paramsShape, components);
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();

                json.WriteStartObject("responses");
                json.WriteStartObject("200");
                json.WriteString("description", "The result, wrapped as {\"ok\":true,\"result\":...}.");
                json.WriteStartObject("content");
                json.WriteStartObject("application/json");
                json.WritePropertyName("schema");
                json.WriteStartObject();
                json.WriteString("type", "object");
                json.WriteStartObject("properties");
                json.WriteStartObject("ok");
                json.WriteString("type", "boolean");
                json.WriteEndObject();
                json.WritePropertyName("result");
                WriteRef(json, resultShape, components);
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteStartObject("default");
                json.WriteString("description", "An error: {\"ok\":false,\"error\":{\"code\":...,\"message\":...,\"retryable\":...,\"details\":...}}. 400 invalid_request, 401 unauthorized, 403 forbidden, 404 not_found, 409 conflict / busy, 413 too_large, 429 rate_limited, 501 unsupported, 503 unavailable, 500 internal.");
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
                json.WriteEndObject();
            }

            json.WriteEndObject();

            json.WriteStartObject("components");
            json.WriteStartObject("securitySchemes");
            json.WriteStartObject("bearer");
            json.WriteString("type", "http");
            json.WriteString("scheme", "bearer");
            json.WriteString("description", "An API key bound to a role (viewer or member). Get one with gateway.getKey on the device (owner, local only).");
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteStartObject("schemas");
            // 组件在写 paths 时才收集;循环到收集不再增长为止(嵌套类型会继续追加)。
            var written = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                var pending = components.Where(c => !written.Contains(c.Key)).ToList();
                if (pending.Count == 0)
                {
                    break;
                }

                foreach (var (name, write) in pending)
                {
                    written.Add(name);
                    json.WritePropertyName(name);
                    write(json);
                }
            }

            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteRef(Utf8JsonWriter json, ITypeShape shape, SortedDictionary<string, Action<Utf8JsonWriter>> components)
    {
        switch (shape.Kind)
        {
            case TypeShapeKind.Object when shape is IObjectTypeShape:
                var name = shape.Type.Name;
                if (!components.ContainsKey(name))
                {
                    components[name] = w => WriteObject(w, shape, components);
                }

                json.WriteStartObject();
                json.WriteString("$ref", $"#/components/schemas/{name}");
                json.WriteEndObject();
                break;
            default:
                WriteInline(json, shape, components);
                break;
        }
    }

    private static void WriteInline(Utf8JsonWriter json, ITypeShape shape, SortedDictionary<string, Action<Utf8JsonWriter>> components)
    {
        var type = Nullable.GetUnderlyingType(shape.Type) ?? shape.Type;
        switch (shape.Kind)
        {
            case TypeShapeKind.Enumerable when shape is IEnumerableTypeShape list:
                if (type == typeof(byte[]))
                {
                    json.WriteStartObject();
                    json.WriteString("type", "string");
                    json.WriteString("contentEncoding", "base64");
                    json.WriteEndObject();
                    break;
                }

                json.WriteStartObject();
                json.WriteString("type", "array");
                json.WritePropertyName("items");
                WriteRef(json, list.ElementType, components);
                json.WriteEndObject();
                break;
            case TypeShapeKind.Dictionary when shape is IDictionaryTypeShape dict:
                json.WriteStartObject();
                json.WriteString("type", "object");
                json.WritePropertyName("additionalProperties");
                WriteRef(json, dict.ValueType, components);
                json.WriteEndObject();
                break;
            case TypeShapeKind.Optional when shape is IOptionalTypeShape optional:
                json.WriteStartObject();
                json.WriteStartArray("anyOf");
                WriteRef(json, optional.ElementType, components);
                json.WriteStartObject();
                json.WriteString("type", "null");
                json.WriteEndObject();
                json.WriteEndArray();
                json.WriteEndObject();
                break;
            case TypeShapeKind.Enum:
                json.WriteStartObject();
                json.WriteString("type", "string");
                json.WriteEndObject();
                break;
            default:
                json.WriteStartObject();
                if (type == typeof(string))
                {
                    json.WriteString("type", "string");
                }
                else if (type == typeof(bool))
                {
                    json.WriteString("type", "boolean");
                }
                else if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
                {
                    json.WriteString("type", "number");
                }
                else if (type.IsPrimitive)
                {
                    json.WriteString("type", "integer");
                }

                json.WriteEndObject();
                break;
        }
    }

    private static void WriteObject(Utf8JsonWriter json, ITypeShape shape, SortedDictionary<string, Action<Utf8JsonWriter>> components)
    {
        json.WriteStartObject();
        json.WriteString("type", "object");
        if (shape is IObjectTypeShape obj)
        {
            json.WriteStartObject("properties");
            foreach (var property in obj.Properties)
            {
                json.WritePropertyName(property.Name);
                WriteRef(json, property.PropertyType, components);
            }

            json.WriteEndObject();

            // 线上格式:构造函数里没有默认值的参数必须出现(可空的写 null),缺键是 invalid_request;带默认值的可以省略。
            var required = obj.Constructor?.Parameters.Where(static p => p.IsRequired).Select(static p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? [.. obj.Properties.Select(static p => p.Name)];
            json.WriteStartArray("required");
            foreach (var property in obj.Properties.Where(p => required.Contains(p.Name)))
            {
                json.WriteStringValue(property.Name);
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }
}
