using Ncw.Agent.Browser.Launch;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Browser.Tests;

/// <summary>找浏览器:使用设备上已有的 Chrome / Edge / Chromium;找不到就给结构化错误,绝不自动下载。</summary>
public sealed class LocatorTests
{
    private static EnvironmentFacts Facts(EnvironmentOs os, string home = "/Users/dev", string user = "dev") => new()
    {
        Os = os,
        OsVersion = "1",
        Shell = os == EnvironmentOs.Windows ? "cmd.exe" : "/bin/sh",
        Home = home,
        Hostname = "h",
        Username = user,
    };

    private static ScriptedFsEnvironment Env(EnvironmentOs os, Func<string, bool> exists, string user = "dev") =>
        new(new LocalEnvironment("dev-1"), Facts(os, user: user), exists);

    [Fact]
    public async Task Nothing_installed_is_a_structured_not_installed_error_that_says_no_download_happens()
    {
        var locator = new BrowserLocator(_ => null);
        var e = await Assert.ThrowsAsync<BrowserException>(() => locator.LocateAsync(Env(EnvironmentOs.MacOS, _ => false), null, CancellationToken.None));
        Assert.Equal(BrowserErrorKeys.NotInstalled, e.Key);
        Assert.Contains("never download", e.Message);
        Assert.Contains("Do not retry", e.Message);
    }

    [Fact]
    public async Task On_macOS_chrome_wins_over_edge_over_chromium()
    {
        var locator = new BrowserLocator(_ => null);
        var all = Env(EnvironmentOs.MacOS, _ => true);
        Assert.Equal("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", (await locator.LocateAsync(all, null, CancellationToken.None)).Value);

        var edgeOnly = Env(EnvironmentOs.MacOS, p => p.Contains("Microsoft Edge", StringComparison.Ordinal));
        Assert.EndsWith("Microsoft Edge", (await locator.LocateAsync(edgeOnly, null, CancellationToken.None)).Value);

        var userApp = Env(EnvironmentOs.MacOS, p => p.StartsWith("/Users/dev/Applications/Chromium.app", StringComparison.Ordinal));
        Assert.StartsWith("/Users/dev/Applications/Chromium.app", (await locator.LocateAsync(userApp, null, CancellationToken.None)).Value);
    }

    [Fact]
    public async Task On_Linux_the_search_path_is_used_in_order_and_fixed_locations_are_a_fallback()
    {
        var locator = new BrowserLocator(name => name == "PATH" ? "/opt/bin:/usr/local/bin" : null);
        var onPath = Env(EnvironmentOs.Linux, p => p == "/usr/local/bin/chromium");
        Assert.Equal("/usr/local/bin/chromium", (await locator.LocateAsync(onPath, null, CancellationToken.None)).Value);

        var fixedOnly = Env(EnvironmentOs.Linux, p => p == "/snap/bin/chromium");
        Assert.Equal("/snap/bin/chromium", (await locator.LocateAsync(fixedOnly, null, CancellationToken.None)).Value);
    }

    [Fact]
    public void On_Windows_the_program_files_and_local_app_data_roots_are_used()
    {
        var env = new Dictionary<string, string>
        {
            ["ProgramFiles"] = @"C:\Program Files",
            ["ProgramFiles(x86)"] = @"C:\Program Files (x86)",
            ["LocalAppData"] = @"C:\Users\dev\AppData\Local",
        };
        var candidates = BrowserLocator.Candidates(Facts(EnvironmentOs.Windows), name => env.GetValueOrDefault(name)).ToList();

        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", candidates[0]);
        Assert.Contains(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", candidates);
        Assert.Contains(@"C:\Users\dev\AppData\Local\Google\Chrome\Application\chrome.exe", candidates);
        Assert.Empty(BrowserLocator.Candidates(Facts(EnvironmentOs.Windows), _ => null));
    }

    [Fact]
    public async Task An_explicit_path_is_used_as_is_and_a_missing_one_is_an_error_not_a_silent_fallback()
    {
        var locator = new BrowserLocator(_ => null);
        var env = Env(EnvironmentOs.MacOS, p => p is "/opt/my-chrome" or "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");

        Assert.Equal("/opt/my-chrome", (await locator.LocateAsync(env, "/opt/my-chrome", CancellationToken.None)).Value);

        var e = await Assert.ThrowsAsync<BrowserException>(() => locator.LocateAsync(env, "/opt/typo", CancellationToken.None));
        Assert.Equal(BrowserErrorKeys.ExecutableMissing, e.Key);
        Assert.Contains("/opt/typo", e.Message);
    }

    [Fact]
    public async Task The_environment_variable_override_applies_when_no_explicit_path_is_given()
    {
        var locator = new BrowserLocator(name => name == BrowserLocator.OverrideVariable ? "/opt/from-env" : null);
        var env = Env(EnvironmentOs.Linux, p => p == "/opt/from-env");
        Assert.Equal("/opt/from-env", (await locator.LocateAsync(env, null, CancellationToken.None)).Value);

        var missing = new BrowserLocator(name => name == BrowserLocator.OverrideVariable ? "/opt/gone" : null);
        var e = await Assert.ThrowsAsync<BrowserException>(() => missing.LocateAsync(env, null, CancellationToken.None));
        Assert.Equal(BrowserErrorKeys.ExecutableMissing, e.Key);
    }

    [Fact]
    public async Task A_directory_is_not_an_executable()
    {
        var locator = new BrowserLocator(_ => null);
        var env = new ScriptedFsEnvironment(new LocalEnvironment("dev-1"), Facts(EnvironmentOs.Linux), _ => false);
        await Assert.ThrowsAsync<BrowserException>(() => locator.LocateAsync(env, "/usr", CancellationToken.None));
    }

    [Fact]
    public async Task The_process_launcher_refuses_a_remote_environment_and_launches_nothing()
    {
        var launcher = new ProcessBrowserLauncher(new BrowserLocator(_ => null), TimeSpan.FromSeconds(1));
        var ssh = new SshLikeEnvironment(new LocalEnvironment("dev-1"));

        var e = await Assert.ThrowsAsync<BrowserException>(() => launcher.LaunchAsync(new BrowserLaunchRequest(ssh, EnvPath.Posix("/tmp/p"), "/bin/echo"), CancellationToken.None));

        Assert.Equal(BrowserErrorKeys.RemoteEnvironment, e.Key);
        Assert.Equal(0, ssh.OpenProcessCalls);
    }

    [Fact]
    public async Task A_browser_that_exits_immediately_is_a_launch_failure_with_its_output()
    {
        var env = new LocalEnvironment("dev-1");
        var launcher = new ProcessBrowserLauncher(new BrowserLocator(_ => null), TimeSpan.FromSeconds(10));
        var dir = EnvPath.Parse(Path.Combine(Path.GetTempPath(), "ncw-browser-fail-" + Guid.NewGuid().ToString("N")[..8]), Harness.Style);
        // /bin/echo 立刻退出且不打印 DevTools 行
        Assert.SkipUnless(OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(), "needs /bin/echo");

        var e = await Assert.ThrowsAsync<BrowserException>(() => launcher.LaunchAsync(new BrowserLaunchRequest(env, dir, "/bin/echo"), CancellationToken.None));

        Assert.Equal(BrowserErrorKeys.LaunchFailed, e.Key);
        Assert.Contains("exited during startup", e.Message);
    }
}
