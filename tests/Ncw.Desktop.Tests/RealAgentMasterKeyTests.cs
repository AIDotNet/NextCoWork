using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Ncw.Desktop.Sidecar;

namespace Ncw.Desktop.Tests;

/// <summary>
/// 真实冒烟:数据根里有加密数据却没有可用的主密钥时(docs/design/02 A6.1),真的 ncw-agent 进程拒绝启动、绝不生成新密钥,
/// 输出恰好一行结构化的 <c>startup-failed</c>(只有 key 和字段名,没有堆栈、没有任何密钥内容),退出码 65;真的守护据此直接停在失败(不当崩溃去重启),
/// 把密钥放回去再点重试就恢复。用 <c>NCW_AGENT_PATH</c>(例如 AOT 发布产物);没有就找刚构建出来的 Agent;都没有或 <c>NCW_SKIP_AGENT_SMOKE=1</c> 时跳过。
/// </summary>
public class RealAgentMasterKeyTests
{
    private const string Sentinel = "SENTINEL-KEY-CONTENT-9f3a1c77";

    private static string KeyFile(string root) => Path.Combine(root, "credential.key");

    private static async Task<(int Exit, string Out, string Err)> RunOnceAsync(string agent, string root)
    {
        var info = new ProcessStartInfo(agent, ["--mode", "local", "--data-root", root])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            Environment = { ["NCW_LOCAL_TOKEN"] = "real-agent-test-token" },
        };
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>真的启动一次全新的数据根(生成主密钥、设备身份、库),再优雅地关掉它。</summary>
    private static async Task SeedAsync(string agent, string root)
    {
        var info = new ProcessStartInfo(agent, ["--mode", "local", "--data-root", root, "--lifeline", "stdin"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            Environment = { ["NCW_LOCAL_TOKEN"] = "real-agent-test-token" },
        };
        using var process = Process.Start(info)!;
        var ready = new TaskCompletionSource();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data?.StartsWith("ncw-agent ready", StringComparison.Ordinal) == true)
            {
                ready.TrySetResult();
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        Assert.Equal(0, process.ExitCode);
    }

    private static void AssertRefusedWithOneLine((int Exit, string Out, string Err) run, string expectedKey, params string[] secrets)
    {
        Assert.Equal(65, run.Exit);
        var all = run.Out + run.Err;
        Assert.Equal($"ncw-agent startup-failed {expectedKey} credential.key", all.Trim());          // 有且仅有这一行
        Assert.DoesNotContain("   at ", all, StringComparison.Ordinal);                               // 没有堆栈
        Assert.DoesNotContain("Unhandled", all, StringComparison.Ordinal);                            // 没有未处理异常
        Assert.DoesNotContain("Exception", all, StringComparison.Ordinal);
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, all, StringComparison.Ordinal);                             // 密钥内容绝不出现
        }
    }

    private static void AddNck1Row(string root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "nextcowork.db"), Pooling = false }.ToString());
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS credentials (ref TEXT PRIMARY KEY, blob BLOB NOT NULL)";
        create.ExecuteNonQuery();
        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO credentials (ref, blob) VALUES ('provider:x', $b)";
        insert.Parameters.AddWithValue("$b", (byte[])[.. "NCK1"u8.ToArray(), 1, .. new byte[12 + 16 + 4]]);
        insert.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_data_root_with_an_identity_but_no_key_is_refused_with_one_structured_line_and_no_key_is_created()
    {
        var agent = RealAgentTests.FindAgent();
        using var dir = new TempDir();
        await SeedAsync(agent, dir.Path);
        File.Delete(KeyFile(dir.Path));

        AssertRefusedWithOneLine(await RunOnceAsync(agent, dir.Path), "agent.masterKey.missing");

        Assert.False(File.Exists(KeyFile(dir.Path))); // 绝不生成新密钥
    }

    [Fact]
    public async Task A_database_with_encrypted_credentials_and_no_key_is_refused_the_same_way()
    {
        var agent = RealAgentTests.FindAgent();
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        AddNck1Row(dir.Path); // 只恢复了 2.x 的库文件:有数据、有加密凭据,没有 credential.key,也没有 device.json

        AssertRefusedWithOneLine(await RunOnceAsync(agent, dir.Path), "agent.masterKey.missing");

        Assert.False(File.Exists(KeyFile(dir.Path)));
        Assert.False(File.Exists(Path.Combine(dir.Path, "device.json")));
    }

    [Fact]
    public async Task A_damaged_key_is_refused_with_its_own_key_never_echoed_and_left_untouched()
    {
        var agent = RealAgentTests.FindAgent();
        using var dir = new TempDir();
        await SeedAsync(agent, dir.Path);
        await File.WriteAllTextAsync(KeyFile(dir.Path), Sentinel);

        AssertRefusedWithOneLine(await RunOnceAsync(agent, dir.Path), "agent.masterKey.unreadable", Sentinel);

        Assert.Equal(Sentinel, await File.ReadAllTextAsync(KeyFile(dir.Path)));
    }

    [Fact]
    public async Task A_wrong_but_well_formed_key_is_refused_as_unreadable_and_its_bytes_never_appear()
    {
        var agent = RealAgentTests.FindAgent();
        using var dir = new TempDir();
        await SeedAsync(agent, dir.Path);
        var wrong = System.Text.Encoding.ASCII.GetBytes(Sentinel.PadRight(32, 'x'));
        await File.WriteAllBytesAsync(KeyFile(dir.Path), wrong);

        AssertRefusedWithOneLine(await RunOnceAsync(agent, dir.Path), "agent.masterKey.unreadable", Sentinel);

        Assert.Equal(wrong, await File.ReadAllBytesAsync(KeyFile(dir.Path)));
    }

    [Fact]
    public async Task A_fresh_data_root_still_starts_normally_and_generates_its_key()
    {
        var agent = RealAgentTests.FindAgent();
        using var dir = new TempDir();

        await SeedAsync(agent, dir.Path); // 退出码 0 在 SeedAsync 里断言

        Assert.True(File.Exists(KeyFile(dir.Path)));
        Assert.Equal(32, new FileInfo(KeyFile(dir.Path)).Length);
        Assert.True(File.Exists(Path.Combine(dir.Path, "device.json")));
    }

    [Fact]
    public async Task The_real_supervisor_stops_at_failed_within_seconds_without_restarting_and_recovers_when_the_key_is_put_back()
    {
        var agent = RealAgentTests.FindAgent();
        using var dir = new TempDir();
        await SeedAsync(agent, dir.Path);
        var keyBackup = await File.ReadAllBytesAsync(KeyFile(dir.Path));
        File.Delete(KeyFile(dir.Path));

        var (sidecar, _, _) = RealAgentTests.Real(dir.Path, agent);
        var statuses = new List<SidecarStatus>();
        sidecar.StatusChanged += s =>
        {
            lock (statuses)
            {
                statuses.Add(s);
            }
        };
        var started = Stopwatch.StartNew();
        sidecar.Start();
        try
        {
            await Rig.Until(() => sidecar.Status is { Phase: SidecarPhase.Failed, Issue: SidecarIssue.MasterKeyMissing }, "master key missing reported", 20_000);
            var tookMs = started.ElapsedMilliseconds;
            Assert.True(tookMs < 10_000, $"took {tookMs} ms"); // 不是崩溃循环的几十秒
            Assert.Equal(0, sidecar.Status.RestartCount);
            Assert.Contains("credential.key", sidecar.Status.Detail);
            Assert.Contains(dir.Path, sidecar.Status.Detail);

            await Task.Delay(2500); // 没有退避、没有重启:退避最短 1 秒,这里等得比它长
            lock (statuses)
            {
                Assert.DoesNotContain(statuses, s => s.Phase == SidecarPhase.Restarting);
            }

            Assert.Equal(SidecarPhase.Failed, sidecar.Status.Phase);

            // 用户把 credential.key 放回去,点重试
            await File.WriteAllBytesAsync(KeyFile(dir.Path), keyBackup);
            sidecar.Retry();
            await Rig.Until(() => sidecar.Status.Phase == SidecarPhase.Running, "recovered after retry", 60_000);
            Assert.Equal(SidecarIssue.None, sidecar.Status.Issue);
        }
        finally
        {
            await sidecar.DisposeAsync();
        }
    }
}
