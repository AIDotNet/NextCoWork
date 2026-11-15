using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;

namespace Ncw.Agent.Terminal.Tests;

// 从 Ncw.Agent.Environments.Tests/SshdFixture.cs 复制(测试夹具不跨测试项目引用):用户态 sshd + 最小的 SSH 环境装配。
/// <summary>
/// 用户态 sshd(高位端口、127.0.0.1、临时 host key 与 authorized_keys、只允许公钥)。
/// 系统里没有 <c>/usr/sbin/sshd</c>、sftp-server 或 ssh-keygen 时 <see cref="Available"/> 为假,依赖它的测试跳过(不是失败)。
/// </summary>
internal sealed class SshdInstance : IDisposable
{
    private static readonly string[] SftpServers = ["/usr/libexec/sftp-server", "/usr/lib/openssh/sftp-server", "/usr/lib/ssh/sftp-server", "/usr/libexec/openssh/sftp-server"];
    private readonly StringBuilder _log = new();
    private readonly string _config;
    private Process? _process;

    private SshdInstance(string directory, int port, string clientKey, string encryptedKey, string hostKeyPublic, string config)
    {
        Directory = directory;
        Port = port;
        ClientKey = clientKey;
        EncryptedKey = encryptedKey;
        HostKeyPublic = hostKeyPublic;
        _config = config;
    }

    public static string? SshdPath => File.Exists("/usr/sbin/sshd") ? "/usr/sbin/sshd" : null;

    public static string? SftpServerPath => SftpServers.FirstOrDefault(File.Exists);

    public static string? KeygenPath => new[] { "/usr/bin/ssh-keygen" }.FirstOrDefault(File.Exists);

    /// <summary>设 <c>NCW_TEST_NO_SSHD=1</c> 可以强制按「没有 sshd」运行,用来验证相关测试是跳过而不是失败。</summary>
    public static bool Available => !OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("NCW_TEST_NO_SSHD") != "1" && SshdPath is not null && SftpServerPath is not null && KeygenPath is not null;

    public const string Passphrase = "correct horse";

    public string Directory { get; }

    public int Port { get; }

    /// <summary>无口令的客户端私钥(已写入 authorized_keys)。</summary>
    public string ClientKey { get; }

    /// <summary>口令为 <see cref="Passphrase"/> 的客户端私钥(已写入 authorized_keys)。</summary>
    public string EncryptedKey { get; }

    /// <summary>服务器的主机公钥(<c>ssh-ed25519 AAAA…</c>)。</summary>
    public string HostKeyPublic { get; }

    public string Username { get; } = Environment.UserName;

    public string Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToString();
            }
        }
    }

    public static async Task<SshdInstance> StartAsync(bool debugLog = false, bool passwordAuth = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ncw-sshd-" + Guid.NewGuid().ToString("N")[..10]);
        System.IO.Directory.CreateDirectory(directory);
        Chmod(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var hostKey = Path.Combine(directory, "host_ed25519");
        var clientKey = Path.Combine(directory, "client_ed25519");
        var encryptedKey = Path.Combine(directory, "client_encrypted");
        await Keygen(hostKey, "");
        await Keygen(clientKey, "");
        await Keygen(encryptedKey, Passphrase);
        await File.WriteAllTextAsync(Path.Combine(directory, "authorized_keys"),
            await File.ReadAllTextAsync(clientKey + ".pub") + await File.ReadAllTextAsync(encryptedKey + ".pub"));
        Chmod(Path.Combine(directory, "authorized_keys"), UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var config = Path.Combine(directory, "sshd_config");
        await File.WriteAllTextAsync(config,
            $"Port {port}\nListenAddress 127.0.0.1\nHostKey {hostKey}\nPidFile {Path.Combine(directory, "sshd.pid")}\n"
            + $"AuthorizedKeysFile {Path.Combine(directory, "authorized_keys")}\nStrictModes no\n"
            + $"PasswordAuthentication {(passwordAuth ? "yes" : "no")}\nKbdInteractiveAuthentication no\nPubkeyAuthentication yes\nUsePAM no\nPermitRootLogin no\n"
            + "AllowTcpForwarding yes\nPermitTTY yes\nMaxStartups 500:30:1000\nMaxSessions 100\n"
            // OpenSSH 9.8+ 会惩罚「没认证就断开」的来源地址(首次连接的指纹确认就是这样一次断开),测试里大量新建环境会被拒绝连接
            + "PerSourcePenalties no\n"
            + $"Subsystem sftp {SftpServerPath}\nLogLevel {(debugLog ? "DEBUG1" : "VERBOSE")}\n");
        Chmod(config, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var instance = new SshdInstance(directory, port, clientKey, encryptedKey, (await File.ReadAllTextAsync(hostKey + ".pub")).Trim(), config);
        await instance.StartProcessAsync();
        return instance;
    }

    /// <summary>杀掉 sshd 进程树(含已建立连接的子进程),模拟服务器崩溃或网络断开。</summary>
    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }

        process.Dispose();
    }

    /// <summary>在同一端口、同一 host key 上重新启动。</summary>
    public Task RestartAsync()
    {
        Stop();
        return StartProcessAsync();
    }

    public void Dispose()
    {
        Stop();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task StartProcessAsync()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(SshdPath!, ["-D", "-e", "-f", _config])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        var listening = new TaskCompletionSource();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (_log)
            {
                _log.AppendLine(e.Data);
            }

            if (e.Data.Contains("Server listening on", StringComparison.Ordinal))
            {
                listening.TrySetResult();
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        _process = process;
        var exited = process.WaitForExitAsync();
        var winner = await Task.WhenAny(listening.Task, exited, Task.Delay(10_000));
        if (winner != listening.Task)
        {
            if (winner == exited && Log.Contains("PerSourcePenalties", StringComparison.Ordinal))
            {
                // 老版本 sshd 不认这个选项
                await File.WriteAllTextAsync(_config, (await File.ReadAllTextAsync(_config)).Replace("PerSourcePenalties no\n", "", StringComparison.Ordinal));
                lock (_log)
                {
                    _log.Clear();
                }

                await StartProcessAsync();
                return;
            }

            throw new InvalidOperationException("sshd did not start:\n" + Log);
        }
    }

    private static void Chmod(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, mode);
        }
    }

    private static async Task Keygen(string path, string passphrase)
    {
        using var process = Process.Start(new ProcessStartInfo(KeygenPath!, ["-q", "-t", "ed25519", "-N", passphrase, "-C", "ncw-test", "-f", path])
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("ssh-keygen failed: " + await process.StandardError.ReadToEndAsync());
        }
    }
}

/// <summary>测试共享的 sshd(每个测试类一个)。</summary>
public sealed class SshdFixture : IDisposable
{
    internal SshdInstance? Instance { get; private set; }

    public bool Available => SshdInstance.Available;

    public SshdFixture()
    {
        if (Available)
        {
            Instance = SshdInstance.StartAsync().GetAwaiter().GetResult();
        }
    }

    internal SshdInstance Require()
    {
        Assert.SkipUnless(Available, "no usable /usr/sbin/sshd + sftp-server + ssh-keygen on this machine");
        return Instance!;
    }

    public void Dispose() => Instance?.Dispose();
}

internal sealed class ScriptedHostKeyCallback(bool approve = true) : IHostKeyCallback
{
    public List<HostKeyInfo> Asked { get; } = [];

    public List<HostKeyInfo> Changed { get; } = [];

    public ValueTask<bool> ConfirmNewHostAsync(HostKeyInfo presented, CancellationToken ct)
    {
        lock (Asked)
        {
            Asked.Add(presented);
        }

        return ValueTask.FromResult(approve);
    }

    public ValueTask NotifyChangedAsync(HostKeyInfo presented, IReadOnlyList<PinnedHostKey> pinned, CancellationToken ct)
    {
        lock (Changed)
        {
            Changed.Add(presented);
        }

        return ValueTask.CompletedTask;
    }
}

internal static class SshTestKit
{
    public static SshEnvironmentProfile ManualProfile(SshdInstance sshd, string? identityFile = null, string? proxyJump = null, SshAuthMethod auth = SshAuthMethod.Key) => new()
    {
        Name = "test-host",
        Target = new SshTarget.Manual("127.0.0.1", sshd.Port, sshd.Username, identityFile ?? sshd.ClientKey, proxyJump),
        AuthMethod = auth,
        ConnectTimeout = TimeSpan.FromSeconds(20),
    };

    public static SshEnvironmentServices Services(SshdInstance sshd, IHostKeyStore? store = null, IHostKeyCallback? callback = null, ISshCredentialProvider? credentials = null) => new()
    {
        HostKeys = store ?? new InMemoryHostKeyStore(),
        HostKeyCallback = callback ?? new ScriptedHostKeyCallback(),
        Credentials = credentials,
        // home 指向临时目录:绝不读用户真实的 ~/.ssh
        ConfigFiles = new PhysicalSshConfigFiles(sshd.Directory),
    };

    public static SshEnvironment NewEnv(SshdInstance sshd, SshEnvironmentProfile? profile = null, SshEnvironmentServices? services = null, SshEnvironmentOptions? options = null) =>
        new(new EnvironmentId("dev", "ssh-test"), profile ?? ManualProfile(sshd), services ?? Services(sshd), options ?? new SshEnvironmentOptions
        {
            Reconnect = new ReconnectPolicy { InitialDelay = TimeSpan.FromMilliseconds(100), MaxDelay = TimeSpan.FromMilliseconds(400), Jitter = 0 },
            KillGrace = TimeSpan.FromMilliseconds(500),
            ReadyWait = TimeSpan.FromSeconds(10),
        });

    public static EnvPath Remote(string path) => EnvPath.Posix(path);
}
