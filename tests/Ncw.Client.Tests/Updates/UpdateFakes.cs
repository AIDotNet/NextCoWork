using System.Text;
using System.Text.Json;
using Ncw.Client.Stores;
using Ncw.Client.Tests.Accounts;
using Ncw.Client.Updates;
using Ncw.Core.Domain;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Ncw.Client.Tests.Updates;

// 自更新测试的脚本化后端:签名工具(和 tools/Ncw.Build 同一种清单形状)、假引擎、假宿主、手动时钟。
// 同时被 Ncw.UI.Tests 以链接方式引用(截图用)。

/// <summary>一把测试用的 Ed25519 密钥,和用它签出来的清单。</summary>
public sealed class TestSigningKey
{
    private readonly Ed25519PrivateKeyParameters _private;

    public TestSigningKey(string keyId)
    {
        KeyId = keyId;
        var generator = new Ed25519KeyPairGenerator();
        generator.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = generator.GenerateKeyPair();
        _private = (Ed25519PrivateKeyParameters)pair.Private;
        PublicKey = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
    }

    public string KeyId { get; }

    public byte[] PublicKey { get; }

    public ReleaseKeyRing Ring => new(new Dictionary<string, byte[]> { [KeyId] = PublicKey });

    public byte[] Sign(byte[] data)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _private);
        signer.BlockUpdate(data, 0, data.Length);
        return signer.GenerateSignature();
    }

    public string SignBase64(byte[] data) => Convert.ToBase64String(Sign(data)) + "\n";
}

public static class UpdateKit
{
    public static ReleaseManifest Manifest(
        string version, string channel = "stable", bool mandatory = false, string? minimum = null, DateTimeOffset? grace = null, string? notes = null,
        string? keyId = "test-1", string status = "signed", string product = "NextCoWork", int schema = 1, params ManifestArtifact[] artifacts) =>
        new(schema, product, version, channel, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero),
            new ManifestPolicy(mandatory, minimum, grace), notes, artifacts,
            new ManifestSigning("ed25519", keyId, status, status == "signed" ? "release-manifest.json.sig" : null));

    /// <summary>和 <c>tools/Ncw.Build</c> 写出来的清单同一形状(camelCase、缩进);手写,不用反射式序列化。</summary>
    public static byte[] Bytes(ReleaseManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", manifest.Schema);
            w.WriteString("product", manifest.Product);
            w.WriteString("version", manifest.Version);
            w.WriteString("channel", manifest.Channel);
            w.WriteString("createdUtc", manifest.CreatedUtc);
            w.WriteStartObject("policy");
            w.WriteBoolean("mandatory", manifest.Policy.Mandatory);
            w.WriteString("minimumSupportedVersion", manifest.Policy.MinimumSupportedVersion);
            if (manifest.Policy.GraceUntil is { } grace)
            {
                w.WriteString("graceUntil", grace);
            }
            else
            {
                w.WriteNull("graceUntil");
            }

            w.WriteEndObject();
            w.WriteString("notes", manifest.Notes);
            w.WriteStartArray("artifacts");
            foreach (var a in manifest.Artifacts)
            {
                w.WriteStartObject();
                w.WriteString("platform", a.Platform);
                w.WriteString("kind", a.Kind);
                w.WriteString("fileName", a.FileName);
                w.WriteString("sha256", a.Sha256);
                w.WriteNumber("size", a.Size);
                w.WriteString("url", a.Url);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartObject("signing");
            w.WriteString("algorithm", manifest.Signing.Algorithm);
            w.WriteString("keyId", manifest.Signing.KeyId);
            w.WriteString("status", manifest.Signing.Status);
            w.WriteString("signatureFile", manifest.Signing.SignatureFile);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return [.. stream.ToArray(), (byte)'\n'];
    }

    /// <summary>清单字节 + 分离签名(base64 文本),和 <c>manifest</c> 子命令写出来的文件同一形状。</summary>
    public static (byte[] Manifest, string Signature) Signed(TestSigningKey key, ReleaseManifest manifest)
    {
        var bytes = Bytes(manifest);
        return (bytes, key.SignBase64(bytes));
    }

    public static ManifestArtifact Artifact(string fileName, string kind = "velopack-full", string sha256 = "00", string platform = "osx-arm64") =>
        new(platform, kind, fileName, sha256, 1024, null);
}

/// <summary>内存 feed:文件名 → 字节。</summary>
public sealed class MemoryFeed : IFeedReader
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public int Reads { get; private set; }

    public Exception? Fail { get; set; }

    public Task<byte[]?> ReadAsync(string name, int maxBytes, CancellationToken ct)
    {
        Reads++;
        if (Fail is not null)
        {
            throw Fail;
        }

        return Task.FromResult(Files.TryGetValue(name, out var bytes) ? bytes : null);
    }

    public void Publish(TestSigningKey key, ReleaseManifest manifest)
    {
        var (bytes, signature) = UpdateKit.Signed(key, manifest);
        Files[FeedLocation.ManifestName] = bytes;
        Files[FeedLocation.SignatureName] = Encoding.ASCII.GetBytes(signature);
    }
}

/// <summary>脚本化的引擎:检查 / 下载 / 交接都能按测试要求成功、失败、挂起。</summary>
public sealed class FakeUpdateEngine : IUpdateEngine
{
    public bool IsInstalled { get; set; } = true;

    public string CurrentVersion { get; set; } = "3.0.0";

    /// <summary>检查的结果;为 null 时抛 <see cref="CheckFailure"/>。</summary>
    public Func<UpdateChannel, UpdateCheckResult>? OnCheck { get; set; }

    public UpdateException? CheckFailure { get; set; }

    public UpdateException? DownloadFailure { get; set; }

    public UpdateException? StageFailure { get; set; }

    /// <summary>下载过程中依次上报的进度。</summary>
    public int[] ProgressSteps { get; set; } = [10, 50, 50, 30, 100];

    /// <summary>设置后,检查 / 下载会等它完成(测试重叠操作)。</summary>
    public TaskCompletionSource? CheckGate { get; set; }

    public TaskCompletionSource? DownloadGate { get; set; }

    public List<UpdateChannel> Checks { get; } = [];

    public int Downloads { get; private set; }

    public List<UpdateOffer> Staged { get; } = [];

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct)
    {
        Checks.Add(channel);
        if (CheckGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        if (CheckFailure is not null)
        {
            throw CheckFailure;
        }

        return OnCheck?.Invoke(channel) ?? throw new InvalidOperationException("no scripted check result");
    }

    public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct)
    {
        Downloads++;
        foreach (var step in ProgressSteps)
        {
            progress(step);
        }

        if (DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        if (DownloadFailure is not null)
        {
            throw DownloadFailure;
        }
    }

    public void StageRestart(UpdateOffer offer)
    {
        if (StageFailure is not null)
        {
            throw StageFailure;
        }

        Staged.Add(offer);
    }

    /// <summary>便捷:检查返回「有新版本」。</summary>
    public FakeUpdateEngine Offering(ReleaseManifest manifest)
    {
        OnCheck = _ => new UpdateCheckResult(manifest, new UpdateOffer(manifest.Version, manifest, "handle"));
        return this;
    }

    /// <summary>便捷:检查返回「没有更新」(清单是旧的 / 一样的)。</summary>
    public FakeUpdateEngine Offering(ReleaseManifest manifest, bool offer)
    {
        OnCheck = _ => new UpdateCheckResult(manifest, offer ? new UpdateOffer(manifest.Version, manifest, "handle") : null);
        return this;
    }
}

/// <summary>记录收尾和退出顺序的假宿主。</summary>
public sealed class FakeUpdateHost(List<string>? journal = null) : IUpdateHost
{
    public List<string> Journal { get; } = journal ?? [];

    public bool PrepareFails { get; set; }

    public int ExitRequests { get; private set; }

    public Task PrepareForRestartAsync(CancellationToken ct)
    {
        Journal.Add("prepare");
        return PrepareFails ? throw new InvalidOperationException("sidecar did not stop") : Task.CompletedTask;
    }

    public void RequestExit()
    {
        ExitRequests++;
        Journal.Add("exit");
    }
}

/// <summary>一整套:Store + 假引擎 + 假宿主 + 手动时钟 + 内存的更新记录,和跑在它们上面的真实 <see cref="UpdateService"/>。</summary>
public sealed class UpdateRig
{
    public static readonly DateTimeOffset Start = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    public UpdateRig(UpdateOptions? options = null, string current = "3.0.0", ClientStores? stores = null, bool installed = true)
    {
        Stores = stores ?? new ClientStores();
        Clock = new ManualTimeProvider(Start);
        Engine = new FakeUpdateEngine { CurrentVersion = current, IsInstalled = installed };
        Host = new FakeUpdateHost(Journal);
        Meta = new MemoryUpdateMetaStore();
        Service = new UpdateService(Stores, Engine, Host, Meta, null, Clock, options ?? new UpdateOptions { AutoCheck = false }, Logs.Add);
    }

    public ClientStores Stores { get; }

    public ManualTimeProvider Clock { get; }

    public FakeUpdateEngine Engine { get; }

    public FakeUpdateHost Host { get; }

    public MemoryUpdateMetaStore Meta { get; }

    public UpdateService Service { get; }

    public List<string> Journal { get; } = [];

    public List<string> Logs { get; } = [];

    public UpdateState State => Stores.Updates.State;
}
