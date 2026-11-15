using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ncw.Client.Accounts;

/// <summary>
/// 刷新令牌的保存位置(07 §2 第 6 步):系统钥匙串(macOS Keychain / Windows DPAPI;Linux 的 libsecret 未接入)。
/// 平台不支持安全保存时抛 <see cref="PlatformNotSupportedException"/>——调用方<b>拒绝保存并提示,绝不降级为明文文件</b>。
/// </summary>
public interface IRefreshTokenVault
{
    /// <summary>读出刷新令牌;没有返回 null。读不出来(钥匙串坏了、换了机器)抛 <see cref="PlatformNotSupportedException"/> 以外的异常也当作没有。</summary>
    string? Load();

    void Save(string refreshToken);

    void Delete();
}

/// <summary>只在内存里(测试)。</summary>
public sealed class MemoryRefreshTokenVault : IRefreshTokenVault
{
    public string? Stored { get; set; }

    /// <summary>模拟「平台不支持安全保存」。</summary>
    public bool Unsupported { get; set; }

    public string? Load() => Unsupported ? throw new PlatformNotSupportedException("No secure store") : Stored;

    public void Save(string refreshToken)
    {
        if (Unsupported)
        {
            throw new PlatformNotSupportedException("No secure store");
        }

        Stored = refreshToken;
    }

    public void Delete()
    {
        if (!Unsupported)
        {
            Stored = null;
        }
    }
}

/// <summary>账户的非机密记录(随控制端放在客户端目录里):决定、用户资料、团队、选中的团队。<b>不含令牌</b>。</summary>
/// <param name="Mode"><c>offline</c> / <c>authenticated</c>(没有这个文件 = 还没决定)。</param>
public sealed record AccountMeta(int V, string Mode, AccountUser? User, TeamOption[] Teams, string? SelectedTeamId, bool ContextRequired);

public interface IAccountMetaStore
{
    AccountMeta? Load();

    void Save(AccountMeta meta);

    void Clear();
}

public sealed class MemoryAccountMetaStore : IAccountMetaStore
{
    public AccountMeta? Stored { get; set; }

    public AccountMeta? Load() => Stored;

    public void Save(AccountMeta meta) => Stored = meta;

    public void Clear() => Stored = null;
}

/// <summary>客户端目录下的 <c>account.json</c>(0600,原子写)。读不懂 = 当作没有(重新走首次启动)。</summary>
public sealed class JsonFileAccountMetaStore(string path) : IAccountMetaStore
{
    public AccountMeta? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), AccountJson.Default.AccountMeta) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(AccountMeta meta)
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
            JsonSerializer.Serialize(stream, meta, AccountJson.Default.AccountMeta);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 删不掉:下次启动读到的还是「已登录」,但钥匙串里没有刷新令牌 → 按「已过期」处理,不会悄悄登录
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AccountMeta))]
internal sealed partial class AccountJson : JsonSerializerContext;
