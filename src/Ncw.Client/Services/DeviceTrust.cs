using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Client.Stores;
using Ncw.Protocol.Security;

namespace Ncw.Client.Services;

/// <summary>
/// 控制端自己的身份(密钥对)。按设计放在系统钥匙串里(docs/design/09 §4);实现由宿主提供
/// (桌面:macOS 钥匙串 / Windows DPAPI)。同一台电脑上只生成一次。
/// </summary>
public interface IControllerIdentityStore
{
    /// <summary>取身份,没有就生成并保存。平台不支持安全保存时抛 <see cref="PlatformNotSupportedException"/>,绝不降级为明文。</summary>
    PeerIdentity LoadOrCreate();
}

/// <summary>只在内存里的身份(测试、以及没有安全存储的场合)。</summary>
public sealed class EphemeralControllerIdentity(string name = "controller") : IControllerIdentityStore
{
    private readonly Lazy<PeerIdentity> _identity = new(() => PeerIdentity.Create(name));

    public PeerIdentity LoadOrCreate() => _identity.Value;
}

/// <summary>已配对设备的读写位置。</summary>
public interface IDeviceBook
{
    ImmutableArray<SavedDevice> Load();

    void Save(ImmutableArray<SavedDevice> devices);
}

public sealed class MemoryDeviceBook : IDeviceBook
{
    public ImmutableArray<SavedDevice> Saved { get; private set; } = ImmutableArray<SavedDevice>.Empty;

    public ImmutableArray<SavedDevice> Load() => Saved;

    public void Save(ImmutableArray<SavedDevice> devices) => Saved = devices;
}

/// <summary>客户端目录下的 <c>devices.json</c>(0600)。里面没有私钥;路由令牌以后用于中继。</summary>
public sealed class JsonFileDeviceBook(string path) : IDeviceBook
{
    public ImmutableArray<SavedDevice> Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return ImmutableArray<SavedDevice>.Empty;
            }

            var file = JsonSerializer.Deserialize(File.ReadAllText(path), DeviceBookJson.Default.DeviceBookFile);
            return file?.Devices is { } devices ? [.. devices] : ImmutableArray<SavedDevice>.Empty;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return ImmutableArray<SavedDevice>.Empty;
        }
    }

    public void Save(ImmutableArray<SavedDevice> devices)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            JsonSerializer.Serialize(stream, new DeviceBookFile(1, [.. devices]), DeviceBookJson.Default.DeviceBookFile);
        }

        File.Move(temp, path, overwrite: true);
    }
}

public sealed record DeviceBookFile(int V, SavedDevice[] Devices);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DeviceBookFile))]
internal sealed partial class DeviceBookJson : JsonSerializerContext;
