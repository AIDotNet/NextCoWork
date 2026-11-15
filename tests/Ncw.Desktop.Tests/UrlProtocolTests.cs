using System.IO.Pipes;
using System.Text;
using Ncw.Desktop;

namespace Ncw.Desktop.Tests;

/// <summary>
/// <c>nextcowork://</c> 的桌面侧:单实例转发(真实的命名管道)与各平台注册的内容。
/// 没验证的部分(需要真机):macOS 的 OpenUri 激活、Windows 注册表写入的真实生效、Linux 的 xdg-mime 和 AppImage、浏览器真的把链接交给应用。
/// </summary>
public sealed class UrlProtocolTests
{
    private static string NewPipe() => "ncw-test-" + Guid.NewGuid().ToString("N")[..12];

    private static string NewLock() => Path.Combine(Path.GetTempPath(), "ncw-lock-" + Guid.NewGuid().ToString("N") + ".lock");

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    // ───────────────────────── 单实例转发 ─────────────────────────

    [Fact]
    public async Task A_second_process_forwards_its_link_to_the_running_instance()
    {
        var pipe = NewPipe();
        var lockPath = NewLock();
        var received = new List<string>();
        await using var primary = SingleInstance.TryBecomePrimary(pipe, lockPath, m => { lock (received) { received.Add(m); } });
        Assert.NotNull(primary);

        // 已经有主实例:第二个拿不到主实例资格,而是把参数转过去
        Assert.Null(SingleInstance.TryBecomePrimary(pipe, lockPath, _ => { }));
        Assert.True(SingleInstance.TrySend(pipe, SingleInstance.LinkPrefix + "nextcowork://auth/callback?code=c&state=s", TimeSpan.FromSeconds(3)));
        Assert.True(SingleInstance.TrySend(pipe, SingleInstance.ActivateMessage, TimeSpan.FromSeconds(3)));

        await WaitUntil(() => { lock (received) { return received.Count == 2; } }, "both messages");
        lock (received)
        {
            Assert.Equivalent(new[] { "link nextcowork://auth/callback?code=c&state=s", "activate" }, received); // 转发是并发处理的:到达顺序不保证
        }
    }

    [Fact]
    public void Sending_with_no_running_instance_reports_failure_quickly()
    {
        Assert.False(SingleInstance.TrySend(NewPipe(), "activate", TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task Oversized_or_malformed_messages_are_dropped_and_the_listener_keeps_working()
    {
        var pipe = NewPipe();
        var lockPath = NewLock();
        var received = new List<string>();
        await using var primary = SingleInstance.TryBecomePrimary(pipe, lockPath, m => { lock (received) { received.Add(m); } });
        Assert.NotNull(primary);

        // 超长(没有换行,超过上限):丢弃
        Assert.False(SingleInstance.TrySend(pipe, new string('a', SingleInstance.MaxMessageBytes + 10), TimeSpan.FromSeconds(1))); // 发送端自己就拒绝
        await RawSendAsync(pipe, new byte[SingleInstance.MaxMessageBytes + 100].Select(_ => (byte)'x').ToArray());
        // 不是 UTF-8
        await RawSendAsync(pipe, [0xFF, 0xFE, 0xFD, (byte)'\n']);
        // 空行
        await RawSendAsync(pipe, [(byte)'\n']);

        Assert.True(SingleInstance.TrySend(pipe, "activate", TimeSpan.FromSeconds(3)));
        await WaitUntil(() => { lock (received) { return received.Count > 0; } }, "the good message");
        Assert.Equal(["activate"], received); // 前面三条什么都没有触发
    }

    [Fact]
    public async Task Only_the_first_line_of_a_message_is_used()
    {
        var pipe = NewPipe();
        var lockPath = NewLock();
        var received = new List<string>();
        await using var primary = SingleInstance.TryBecomePrimary(pipe, lockPath, m => { lock (received) { received.Add(m); } });
        await RawSendAsync(pipe, Encoding.UTF8.GetBytes("link nextcowork://pair?invite=x\nlink nextcowork://auth/callback?code=evil&state=evil\n"));
        await WaitUntil(() => { lock (received) { return received.Count > 0; } }, "the first line");
        Assert.Equal(["link nextcowork://pair?invite=x"], received);
    }

    [Fact]
    public async Task After_the_primary_exits_another_process_can_take_over()
    {
        var pipe = NewPipe();
        var lockPath = NewLock();
        var first = SingleInstance.TryBecomePrimary(pipe, lockPath, _ => { });
        Assert.NotNull(first);
        await first!.DisposeAsync();

        await using var second = SingleInstance.TryBecomePrimary(pipe, lockPath, _ => { });
        Assert.NotNull(second);
    }

    [Fact]
    public void The_pipe_name_depends_on_the_client_directory_and_is_stable()
    {
        var a = SingleInstance.PipeName("/tmp/ncw-a");
        Assert.Equal(a, SingleInstance.PipeName("/tmp/ncw-a"));
        Assert.NotEqual(a, SingleInstance.PipeName("/tmp/ncw-b"));
        Assert.StartsWith("ncw-", a);
    }

    private static async Task RawSendAsync(string pipe, byte[] bytes)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(3000);
            await client.WriteAsync(bytes);
            await client.FlushAsync();
        }
        catch (IOException)
        {
            // 对端读够了就关:大消息会遇到
        }
    }

    // ───────────────────────── 注册内容 ─────────────────────────

    [Fact]
    public void The_Windows_registration_points_the_scheme_at_the_executable_with_the_link_as_the_argument()
    {
        var entries = UrlSchemeRegistration.WindowsEntries(@"C:\Users\me\AppData\Local\NextCoWork\NextCoWork.exe");
        Assert.Contains((@"Software\Classes\nextcowork", (string?)null, "URL:NextCoWork"), entries);
        Assert.Contains((@"Software\Classes\nextcowork", "URL Protocol", ""), entries);
        Assert.Contains(entries, e => e.SubKey.EndsWith(@"shell\open\command", StringComparison.Ordinal) && e.Value == "\"C:\\Users\\me\\AppData\\Local\\NextCoWork\\NextCoWork.exe\" \"%1\"");
        Assert.All(entries, e => Assert.StartsWith(@"Software\Classes\nextcowork", e.SubKey)); // 只写自己的 scheme,HKCU 下
    }

    [Theory]
    [InlineData("/opt/NextCoWork/NextCoWork", "/opt/NextCoWork/NextCoWork")]
    [InlineData("/home/me/My Apps/NextCoWork.AppImage", "\"/home/me/My Apps/NextCoWork.AppImage\"")]
    [InlineData("/tmp/a\"b", "\"/tmp/a\\\"b\"")]
    [InlineData("/tmp/$HOME/x", "\"/tmp/\\$HOME/x\"")]
    public void The_exec_path_is_quoted_per_the_desktop_entry_rules(string path, string expected) =>
        Assert.Equal(expected, UrlSchemeRegistration.QuoteExec(path));

    [Fact]
    public void The_Linux_desktop_entry_declares_the_scheme_handler_and_takes_the_link_as_an_argument()
    {
        var entry = UrlSchemeRegistration.LinuxDesktopEntry("/opt/NextCoWork/NextCoWork");
        Assert.Contains("Exec=/opt/NextCoWork/NextCoWork %u", entry);
        Assert.Contains("MimeType=x-scheme-handler/nextcowork;", entry);
        Assert.Contains("NoDisplay=true", entry); // 不出现在应用菜单里
    }

    [Fact]
    public void Writing_the_Linux_entry_is_idempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ncw-apps-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.True(UrlSchemeRegistration.WriteLinuxEntry(dir, "/opt/NextCoWork/NextCoWork", runXdgMime: false));
            Assert.False(UrlSchemeRegistration.WriteLinuxEntry(dir, "/opt/NextCoWork/NextCoWork", runXdgMime: false)); // 内容没变:不动
            Assert.True(UrlSchemeRegistration.WriteLinuxEntry(dir, "/opt/other/NextCoWork", runXdgMime: false));
            Assert.Contains("/opt/other/NextCoWork", File.ReadAllText(Path.Combine(dir, UrlSchemeRegistration.DesktopFileName)));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Registration_is_skipped_for_the_dotnet_host_and_when_disabled()
    {
        Environment.SetEnvironmentVariable("NCW_REGISTER_URL_SCHEME", "0");
        try
        {
            Assert.Equal("disabled", UrlSchemeRegistration.EnsureRegistered());
        }
        finally
        {
            Environment.SetEnvironmentVariable("NCW_REGISTER_URL_SCHEME", null);
        }

        // 测试进程是 testhost(dotnet):不能把 dotnet 注册成处理程序,也不能动用户的系统注册
        Assert.Contains(UrlSchemeRegistration.EnsureRegistered(), new[] { "dotnet-host", "bundle" });
    }
}
