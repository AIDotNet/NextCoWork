using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host;

/// <summary>设备身份文件。M2 用随机 id;接入 TLS(局域网承载)后改为设备公钥的哈希(docs/design/09 §4)。</summary>
public sealed record DeviceIdentityFile(string DeviceId, string Name, long CreatedAt);

/// <summary>项目登记文件。接入 2.x 数据接管后改为数据库(D13),这里只是 M2 的过渡存储。</summary>
public sealed record ProjectsFile(int Version, long Revision, ProjectDto[] Projects);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceIdentityFile))]
[JsonSerializable(typeof(ProjectsFile))]
internal sealed partial class AgentJsonContext : JsonSerializerContext;

internal static class AtomicFile
{
    /// <summary>先写临时文件再改名,写到一半崩溃也不会留下坏文件。</summary>
    public static void Write(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = File.Create(temp))
        {
            write(stream);
        }

        File.Move(temp, path, overwrite: true);
    }

    public static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, type);
    }
}
