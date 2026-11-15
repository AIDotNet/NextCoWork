using System.Text;
using System.Text.Json;

namespace Ncw.Agent.Plugins.Tests;

/// <summary>
/// 宿主 API 的每道门(2.x 的「四道门」):每个绑定先查授权(实时)、再收窄参数(路径 / 命令白名单 / 域名)、行为类(执行命令)再要用户批准;文件和进程只经执行环境,网络只经注入的 HTTP。
/// 用 acme.hello 的命令把每个 API 打一遍,失败原因以稳定的 <c>Error.code</c> 回到插件。
/// </summary>
public sealed class HostApiTests : IAsyncDisposable
{
    private readonly PluginFx _fx = new();

    public ValueTask DisposeAsync() => _fx.DisposeAsync();

    private Task<InstalledPlugin> HelloAsync(params string[] grant) => _fx.InstallEnableAsync(PluginFx.FixtureZip("acme.hello"), grant);

    private Task Run(string command, string? args = null) => _fx.Manager.RunCommandAsync("acme.hello", $"acme.hello.{command}", args, "w1");

    private string? Last(string name) => _fx.Stored("acme.hello", $"last.{name}");

    private string? Error(string name) => _fx.Stored("acme.hello", $"last.{name}.error");

    // ───────────────────────── 文件 ─────────────────────────

    [Fact]
    public async Task Reading_and_writing_inside_the_project_works_with_the_matching_permissions_and_returns_a_revision()
    {
        await HelloAsync("workspace.read", "workspace.write");

        await Run("write", """{"path":"docs/new.txt","data":"first"}""");
        await Run("read", """{"path":"docs/new.txt"}""");

        Assert.Equal("true", Last("write"));
        Assert.Equal("first", Last("read"));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(_fx.ProjectDir, "docs", "new.txt")));
    }

    [Fact]
    public async Task Writing_needs_workspace_write_and_reading_alone_does_not_grant_it()
    {
        await HelloAsync("workspace.read");

        await Run("write", """{"path":"a.txt","data":"x"}""");

        Assert.Contains("permission_denied", Error("write"));
        Assert.False(File.Exists(Path.Combine(_fx.ProjectDir, "a.txt")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("a/../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/windows/win.ini")]
    public async Task Paths_that_leave_the_project_are_refused_for_reads_and_writes(string path)
    {
        await HelloAsync("workspace.read", "workspace.write");
        await File.WriteAllTextAsync(Path.Combine(_fx.Root, "outside.txt"), "outside");

        await Run("read", Js.Args(("path", path)));
        var read = Error("read");
        await Run("write", Js.Args(("path", path), ("data", "pwned")));

        Assert.True(read is not null && (read.Contains("rejected", StringComparison.Ordinal) || read.Contains("invalid_argument", StringComparison.Ordinal)), read);
        Assert.Equal("outside", await File.ReadAllTextAsync(Path.Combine(_fx.Root, "outside.txt")));
        Assert.False(File.Exists("/etc/passwd.pwned"));
    }

    [Fact]
    public async Task A_symlink_inside_the_project_that_points_outside_is_refused_realpath_not_string_checks()
    {
        await HelloAsync("workspace.read", "workspace.write");
        await File.WriteAllTextAsync(Path.Combine(_fx.Root, "outside.txt"), "outside");
        Directory.CreateDirectory(Path.Combine(_fx.Root, "outdir"));
        File.CreateSymbolicLink(Path.Combine(_fx.ProjectDir, "file-link"), Path.Combine(_fx.Root, "outside.txt"));
        File.CreateSymbolicLink(Path.Combine(_fx.ProjectDir, "dir-link"), Path.Combine(_fx.Root, "outdir"));

        await Run("read", """{"path":"file-link"}""");
        Assert.Contains("rejected", Error("read"));
        await Run("write", """{"path":"dir-link/planted.txt","data":"x"}""");
        Assert.Contains("rejected", Error("write"));
        Assert.False(File.Exists(Path.Combine(_fx.Root, "outdir", "planted.txt")));
    }

    [Fact]
    public async Task A_file_over_the_plugin_read_limit_and_a_directory_are_invalid_arguments()
    {
        await HelloAsync("workspace.read");
        await File.WriteAllBytesAsync(Path.Combine(_fx.ProjectDir, "big.bin"), new byte[9 * 1024 * 1024]);
        Directory.CreateDirectory(Path.Combine(_fx.ProjectDir, "folder"));

        await Run("read", """{"path":"big.bin"}""");
        Assert.Contains("invalid_argument", Error("read"));
        await Run("read", """{"path":"folder"}""");
        Assert.Contains("invalid_argument", Error("read"));
        await Run("read", """{"path":"missing.txt"}""");
        Assert.Contains("not_found", Error("read"));
    }

    [Fact]
    public async Task The_revision_is_an_optimistic_lock_a_stale_write_is_rejected()
    {
        var zip = PluginFx.MinimalZip("locker", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.locker.go', async () => {
                const first = await ncw.workspace.fs.readFile('doc.txt')
                await ncw.workspace.fs.writeFile('doc.txt', 'mine', { revision: first.revision })       // 没人动过:成功
                let second = 'no error'
                try { await ncw.workspace.fs.writeFile('doc.txt', 'stale', { revision: first.revision }) } catch (e) { second = e.code }   // 过期的 revision
                await ncw.storage.global.set('second', second)
              }))
            }
            """, """ "permissions": ["workspace.read", "workspace.write", "storage"], "contributes": { "commands": [{ "command": "acme.locker.go", "title": "%a%" }] } """);
        await _fx.InstallEnableAsync(zip);
        await File.WriteAllTextAsync(Path.Combine(_fx.ProjectDir, "doc.txt"), "original");

        await _fx.Manager.RunCommandAsync("acme.locker", "acme.locker.go", null, "w1");

        Assert.Equal("rejected", _fx.Stored("acme.locker", "second"));
        Assert.Equal("mine", await File.ReadAllTextAsync(Path.Combine(_fx.ProjectDir, "doc.txt")));
    }

    [Fact]
    public async Task Without_a_workspace_path_capabilities_are_rejected_instead_of_guessing()
    {
        await HelloAsync("workspace.read");

        await _fx.Manager.RunCommandAsync("acme.hello", "acme.hello.read", """{"path":"x"}""", null);

        Assert.Contains("rejected: no workspace is open", Error("read"));
    }

    [Fact]
    public async Task Find_files_supports_prefix_globs_skips_vendor_directories_and_honours_the_limit()
    {
        var zip = PluginFx.MinimalZip("finder", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.finder.go', async (args) => {
                await ncw.storage.global.set('hits', JSON.stringify(await ncw.workspace.findFiles(args.glob, args.limit)))
              }))
            }
            """, """ "permissions": ["workspace.read", "storage"], "contributes": { "commands": [{ "command": "acme.finder.go", "title": "%a%" }] } """);
        await _fx.InstallEnableAsync(zip);
        foreach (var file in new[] { "src/a.ts", "src/deep/b.ts", "src/deep/c.js", "docs/readme.md", "docs/guide.md", "node_modules/pkg/x.ts", "top.ts" })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_fx.ProjectDir, file))!);
            await File.WriteAllTextAsync(Path.Combine(_fx.ProjectDir, file), "x");
        }

        async Task<string?> Find(string glob, int? limit = null)
        {
            await _fx.Manager.RunCommandAsync("acme.finder", "acme.finder.go", Js.Args(("glob", glob), ("limit", limit)), "w1");
            return _fx.Stored("acme.finder", "hits");
        }

        Assert.Equal("""["src/a.ts","src/deep/b.ts","src/deep/c.js"]""", await Find("src/**"));
        Assert.Equal("""["docs/guide.md","docs/readme.md"]""", await Find("docs/*.md"));
        Assert.Equal("""["src/a.ts","src/deep/b.ts","top.ts"]""", await Find("**/*.ts")); // node_modules 不进
        Assert.Equal("""["src/a.ts"]""", await Find("src/**", 1));
    }

    [Theory]
    [InlineData("src/**", "src/a/b.ts", true)]
    [InlineData("src/*", "src/a/b.ts", false)]
    [InlineData("src/*.ts", "src/a.ts", true)]
    [InlineData("**/*.ts", "a.ts", true)]
    [InlineData("a?c", "abc", true)]
    [InlineData("a?c", "a/c", false)]
    [InlineData("docs/*.md", "docs/x.txt", false)]
    public void Glob_matching(string glob, string path, bool expected)
    {
        Assert.Equal(expected, FindFiles.Matches(glob, path));
    }

    // ───────────────────────── 命令 ─────────────────────────

    [Fact]
    public async Task Running_a_command_needs_the_permission_the_allow_list_and_the_users_approval_which_names_the_execution_environment()
    {
        await HelloAsync("process");
        var command = Run("exec", """{"command":"echo","args":["hello world","it's"]}""");

        await Wait.UntilAsync(() => _fx.Manager.Snapshot().Interactions.Count == 1, "the approval prompt");
        var prompt = _fx.Manager.Snapshot().Interactions.Single();
        Assert.Equal(PluginInteractionKinds.Exec, prompt.Kind);
        Assert.Equal("echo 'hello world' 'it'\\''s'", prompt.Command);
        Assert.True(prompt.Danger);
        Assert.Equal(new PluginEnvironmentRef("dev", "local", "Local", "local"), prompt.Environment); // 审批带执行环境标识(设备 + 环境)
        _fx.Manager.ReplyInteraction(prompt.RequestId, new PluginInteractionReply(Confirmed: true));
        await command;

        Assert.Equal("""{"code":0,"stdout":"hello world it's"}""", Last("exec"));
        Assert.Empty(_fx.Manager.Snapshot().Interactions);
    }

    [Fact]
    public async Task A_declined_command_never_runs()
    {
        await HelloAsync("process");
        var marker = Path.Combine(_fx.ProjectDir, "ran.txt");
        var command = Run("exec", Js.Args(("command", "git"), ("args", new[] { "init", _fx.ProjectDir + "/declined" })));
        await Wait.UntilAsync(() => _fx.Manager.Snapshot().Interactions.Count == 1, "the approval prompt");

        _fx.Manager.ReplyInteraction(_fx.Manager.Snapshot().Interactions.Single().RequestId, new PluginInteractionReply(Confirmed: false));
        await command;

        Assert.Contains("rejected: the command was not approved", Error("exec"));
        Assert.False(Directory.Exists(Path.Combine(_fx.ProjectDir, "declined")));
        Assert.False(File.Exists(marker));
    }

    [Theory]
    [InlineData("rm", "the manifest command allow-list")]
    [InlineData("/bin/rm", "the manifest command allow-list")]
    [InlineData("echo; rm -rf /", "shell metacharacters")]
    [InlineData("", "command is required")]
    public async Task Commands_outside_the_allow_list_or_with_shell_metacharacters_are_refused_before_any_prompt(string command, string reason)
    {
        await HelloAsync("process");

        await Run("exec", Js.Args(("command", command)));

        Assert.Contains("invalid_argument", Error("exec"));
        Assert.Contains(reason, Error("exec"), StringComparison.Ordinal);
        Assert.Empty(_fx.Manager.Snapshot().Interactions); // 没到审批这一步
    }

    [Fact]
    public async Task Without_the_process_permission_nothing_is_even_asked()
    {
        await HelloAsync();

        await Run("exec", """{"command":"echo","args":["x"]}""");

        Assert.Contains("permission_denied", Error("exec"));
        Assert.Empty(_fx.Manager.Snapshot().Interactions);
    }

    [Fact]
    public async Task Revoking_the_permission_while_the_approval_is_pending_blocks_the_run_the_recheck_after_the_prompt()
    {
        await HelloAsync("process");
        var command = Run("exec", """{"command":"echo","args":["x"]}""");
        await Wait.UntilAsync(() => _fx.Manager.Snapshot().Interactions.Count == 1, "the approval prompt");

        await _fx.Manager.RevokeAsync("acme.hello", ["process"]);
        var prompt = _fx.Manager.Snapshot().Interactions.SingleOrDefault();
        if (prompt is not null)
        {
            _fx.Manager.ReplyInteraction(prompt.RequestId, new PluginInteractionReply(Confirmed: true));
        }

        await command;

        Assert.DoesNotContain("\"code\":0", Last("exec") ?? "", StringComparison.Ordinal); // 批准只说明「他当时点了允许」,能不能真的跑由复检说了算
    }

    // ───────────────────────── 网络 ─────────────────────────

    private static PluginHttpResponse Response(int status, string body, params (string, string)[] headers) =>
        new(status, headers.ToDictionary(h => h.Item1, h => h.Item2), Encoding.UTF8.GetBytes(body), false);

    [Fact]
    public async Task Fetch_goes_through_the_injected_http_only_for_declared_https_hosts_and_returns_data_not_a_stream()
    {
        await HelloAsync("net");
        _fx.Http.Handler = _ => Response(200, "pong", ("Content-Type", "text/plain"));

        await Run("fetch", """{"url":"https://api.example.com/v1/ping?x=1"}""");

        Assert.Equal("""{"status":200,"body":"pong"}""", Last("fetch"));
        var request = Assert.Single(_fx.Http.Requests);
        Assert.Equal(("GET", "https://api.example.com/v1/ping?x=1"), (request.Method, request.Url.ToString()));
        Assert.Contains(_fx.Manager.Activity("acme.hello"), a => a.Method == "net.fetch" && a.Verdict == "ok" && a.Summary == "fetch api.example.com");
    }

    [Theory]
    [InlineData("https://evil.example.org/x", "hostPermissions")]
    [InlineData("http://api.example.com/x", "only https")]
    [InlineData("https://user:pw@api.example.com/x", "credentials")]
    [InlineData("https://api.example.com.evil.org/x", "hostPermissions")]
    [InlineData("file:///etc/passwd", "only https")]
    [InlineData("not a url", "valid URL")]
    public async Task Fetch_to_undeclared_hosts_or_unsafe_urls_is_refused_and_nothing_is_sent(string url, string reason)
    {
        await HelloAsync("net");

        await Run("fetch", Js.Args(("url", url)));

        Assert.Contains("invalid_argument", Error("fetch"));
        Assert.Contains(reason, Error("fetch"), StringComparison.Ordinal);
        Assert.Empty(_fx.Http.Requests);
    }

    [Fact]
    public async Task Fetch_needs_the_net_permission()
    {
        await HelloAsync();

        await Run("fetch", """{"url":"https://api.example.com/x"}""");

        Assert.Contains("permission_denied", Error("fetch"));
        Assert.Empty(_fx.Http.Requests);
    }

    [Fact]
    public async Task Redirects_are_followed_per_hop_within_the_same_host_but_never_across_hosts()
    {
        await HelloAsync("net");
        var hops = 0;
        _fx.Http.Handler = request => request.Url.AbsolutePath switch
        {
            "/start" => Response(302, "", ("Location", "/middle")),
            "/middle" => Response(301, "", ("Location", "https://api.example.com/final")),
            "/final" => Response(200, "arrived"),
            "/leave" => Response(302, "", ("Location", "https://evil.example.org/steal")),
            "/loop" => Response(302, "", ("Location", $"/loop?{++hops}")),
            _ => Response(404, ""),
        };

        await Run("fetch", """{"url":"https://api.example.com/start"}""");
        Assert.Equal("""{"status":200,"body":"arrived"}""", Last("fetch"));

        await Run("fetch", """{"url":"https://api.example.com/leave"}""");
        Assert.Contains("leaves the declared host", Error("fetch"));
        Assert.DoesNotContain(_fx.Http.Requests, r => r.Url.Host == "evil.example.org"); // 跨 host 的跳转根本没发出去

        await Run("fetch", """{"url":"https://api.example.com/loop"}""");
        Assert.Contains("too many redirects", Error("fetch"));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.9", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.169.254", true)]
    [InlineData("localhost", true)]
    [InlineData("printer.local", true)]
    [InlineData("[::1]", true)]
    [InlineData("[fd12::1]", true)]
    [InlineData("fc2.com", false)] // 以 fc 开头的正常域名不是 IPv6 唯一本地地址
    [InlineData("fdroid.org", false)]
    [InlineData("example.com", false)]
    public void Private_and_loopback_hosts_are_recognised_by_literal(string host, bool expected)
    {
        Assert.Equal(expected, HostGuards.IsPrivateHost(host));
    }

    [Fact]
    public void An_author_cannot_declare_a_private_address_in_host_permissions_to_reach_it()
    {
        var result = HostGuards.NarrowFetchUrl(["https://169.254.169.254/*"], "https://169.254.169.254/latest/meta-data");

        Assert.False(result.Allowed);
        Assert.Contains("private", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.example.com/v1/x", "https://api.example.com/*", true)]
    [InlineData("https://api.example.com/v2/x", "https://api.example.com/v1/*", false)]
    [InlineData("https://api.example.com/v1", "https://api.example.com/v1", true)]
    [InlineData("https://api.example.com:8443/x", "https://api.example.com/*", false)]
    [InlineData("https://API.example.com/x", "https://api.example.com/*", true)]
    public void Host_permission_matching(string url, string pattern, bool expected)
    {
        Assert.Equal(expected, HostGuards.MatchesHostPermission([pattern], new Uri(url)));
    }

    // ───────────────────────── 存储 / 密钥 / 其它 ─────────────────────────

    [Fact]
    public async Task Storage_is_private_per_plugin_quota_limited_and_workspace_scoped_keys_do_not_collide_with_global_ones()
    {
        var zip = PluginFx.MinimalZip("keeper", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.keeper.go', async () => {
                await ncw.storage.global.set('k', 'global')
                await ncw.storage.workspace.set('k', 'workspace')
                const report = { g: await ncw.storage.global.get('k'), w: await ncw.storage.workspace.get('k'), gk: await ncw.storage.global.keys(), wk: await ncw.storage.workspace.keys() }
                let bad = 'accepted'
                try { await ncw.storage.global.set('bad key!', 'x') } catch (e) { bad = e.code }
                let big = 'accepted'
                try { await ncw.storage.global.set('big', 'x'.repeat(6 * 1024 * 1024)) } catch (e) { big = e.code }
                await ncw.storage.global.set('report', JSON.stringify({ ...report, bad, big }))
              }))
            }
            """, """ "permissions": ["storage"], "contributes": { "commands": [{ "command": "acme.keeper.go", "title": "%a%" }] } """);
        await _fx.InstallEnableAsync(zip);

        await _fx.Manager.RunCommandAsync("acme.keeper", "acme.keeper.go", null, "w1");

        using var report = JsonDocument.Parse(_fx.Stored("acme.keeper", "report")!);
        var r = report.RootElement;
        Assert.Equal(("global", "workspace"), (r.GetProperty("g").GetString(), r.GetProperty("w").GetString()));
        Assert.Equal(["k"], r.GetProperty("gk").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["k"], r.GetProperty("wk").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(("invalid_argument", "invalid_argument"), (r.GetProperty("bad").GetString(), r.GetProperty("big").GetString()));
        Assert.Null(_fx.Stored("acme.hello", "k")); // 别的插件看不到
    }

    [Fact]
    public async Task Secrets_are_prefixed_by_the_host_so_a_plugin_cannot_read_another_plugins_secret()
    {
        var zip = (string name) => PluginFx.MinimalZip(name, """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.NAME.go', async () => {
                await ncw.secrets.set('token', 'NAME-secret')
                let steal = 'none'
                try { steal = String(await ncw.secrets.get('plugin:acme.other:token')) } catch (e) { steal = e.code }
                await ncw.storage.global.set('seen', steal + '/' + await ncw.secrets.get('token'))
              }))
            }
            """.Replace("NAME", name, StringComparison.Ordinal), $$""" "permissions": ["secrets", "storage"], "contributes": { "commands": [{ "command": "acme.{{name}}.go", "title": "%a%" }] } """);
        await _fx.InstallEnableAsync(zip("other"));
        await _fx.InstallEnableAsync(zip("thief"));

        await _fx.Manager.RunCommandAsync("acme.other", "acme.other.go", null, null);
        await _fx.Manager.RunCommandAsync("acme.thief", "acme.thief.go", null, null);

        Assert.Equal("plugin:acme.other:token", _fx.Secrets.Values.Keys.First(k => k.EndsWith("other:token", StringComparison.Ordinal)));
        Assert.Equal("null/thief-secret", _fx.Stored("acme.thief", "seen")); // 传 'plugin:acme.other:token' 只会被宿主再加上自己的前缀:读不到别人的
        Assert.True(_fx.Secrets.Values.ContainsKey("plugin:acme.thief:token"));
        Assert.Equal("other-secret", _fx.Secrets.Values["plugin:acme.other:token"]);
    }

    [Fact]
    public async Task Calling_a_host_method_that_does_not_exist_is_unknown_method_not_a_permission_error()
    {
        var zip = PluginFx.MinimalZip("typo", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.typo.go', async () => {
                let code = 'none'
                try { await ncw.scm.status() } catch (e) { code = e.name }                      // 不存在的命名空间
                let code2 = 'none'
                try { await ncw.workspace.fs.nope('x') } catch (e) { code2 = e.name }
                await ncw.storage.global.set('r', code + '/' + code2)
              }))
            }
            """, """ "permissions": ["storage"], "contributes": { "commands": [{ "command": "acme.typo.go", "title": "%a%" }] } """);
        await _fx.InstallEnableAsync(zip);

        await _fx.Manager.RunCommandAsync("acme.typo", "acme.typo.go", null, null);

        Assert.Equal("TypeError/TypeError", _fx.Stored("acme.typo", "r")); // 没绑定的 API 根本不存在,不会走到能力表里去
    }

    [Fact]
    public async Task Every_host_call_is_recorded_without_its_arguments_and_the_log_is_a_bounded_ring_newest_first()
    {
        await HelloAsync("workspace.read");
        await File.WriteAllTextAsync(Path.Combine(_fx.ProjectDir, "secret-name.txt"), "top secret content");

        await Run("read", """{"path":"secret-name.txt"}""");

        var entries = _fx.Manager.Activity("acme.hello");
        var read = entries.First(a => a.Method == "workspace.readFile");
        Assert.Equal("ok", read.Verdict);
        Assert.Equal("read secret-name.txt", read.Summary); // 摘要只放「够用来认出是哪一次」的信息
        Assert.DoesNotContain(entries, a => a.Summary.Contains("top secret", StringComparison.Ordinal));
        Assert.Equal(entries.OrderByDescending(a => a.Ts), entries); // 最近的在前
        Assert.Empty(_fx.Manager.Activity("acme.nobody"));
        Assert.Equal(entries.Count, _fx.Manager.Activity().Count);
    }

    [Fact]
    public async Task App_info_and_appearance_need_no_permission()
    {
        var zip = PluginFx.MinimalZip("who", """
            import * as ncw from 'nextcowork'
            export function activate(c) {
              c.subscriptions.push(ncw.commands.registerCommand('acme.who.go', async () => {
                const info = await ncw.env.appInfo()
                await ncw.window.showConfirm   // 只是确认它存在
                globalThis.__seen = [info.appName, info.appVersion, info.language, await ncw.appearance.get(), ncw.version, ncw.extensionId].join('|')
                await ncw.diagnostics.log('warn', 'hello from who')
                await ncw.window.setStatusBarItem('s', 'plugin.acme.who.ready')
                await ncw.window.withProgress({ titleKey: 'x' }, async (p) => { p.report({ fraction: 0.5 }) })
              }))
              c.subscriptions.push(ncw.commands.registerCommand('acme.who.read', async () => { await ncw.diagnostics.log('info', globalThis.__seen) }))
            }
            """, """ "contributes": { "commands": [{ "command": "acme.who.go", "title": "%a%" }, { "command": "acme.who.read", "title": "%b%" }] } """);
        await _fx.InstallEnableAsync(zip);

        await _fx.Manager.RunCommandAsync("acme.who", "acme.who.go", null, null);
        await _fx.Manager.RunCommandAsync("acme.who", "acme.who.read", null, null);

        var log = _fx.Manager.Activity("acme.who").Where(a => a.Method == "diagnostics.log").Select(a => a.Summary).ToList();
        Assert.Contains("info: NextCoWork|3.0.0-test|en-US|light|0.3.3|acme.who", log);
        Assert.Contains(_fx.Plugin("acme.who").Diagnostics, d => d.Key == PluginDiagnosticKeys.PluginLog && d.Message == "hello from who" && d.Level == "warn");
    }
}
