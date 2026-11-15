using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;

namespace Ncw.Agent.Environments.Tests;

/// <summary>~/.ssh/config 解析:纯单测,不碰磁盘。</summary>
public class SshConfigTests
{
    private const string Home = "/home/me";

    private sealed class MemoryFiles(Dictionary<string, string> files) : ISshConfigFiles
    {
        public string Home => SshConfigTests.Home;

        public string? ReadAllText(string path) => files.GetValueOrDefault(path);

        public IEnumerable<string> Glob(string pattern)
        {
            var directory = pattern[..(pattern.LastIndexOf('/') + 1)];
            var filePattern = pattern[(pattern.LastIndexOf('/') + 1)..];
            return files.Keys.Where(k => k.StartsWith(directory, StringComparison.Ordinal) && !k[directory.Length..].Contains('/')
                && SshConfig.Glob(filePattern, k[directory.Length..])).Order(StringComparer.Ordinal);
        }
    }

    private const string Sample = """
        # global defaults
        Host *
            ServerAliveInterval 30
            IdentityFile ~/.ssh/id_default

        Host dev web-*
            HostName dev.example.com
            User alice
            Port 2222
            IdentityFile ~/.ssh/id_dev
            IdentitiesOnly yes
            ProxyJump bastion

        Host bastion
            HostName bastion.example.com
            User jump

        Host !secret *.example.com
            User wildcard
            Port 2200
        """;

    [Fact]
    public void Resolves_host_name_user_port_identity_and_flags()
    {
        var host = SshConfig.Parse(Sample, home: Home).Resolve("dev", localUser: "local");
        Assert.Equal("dev", host.Alias);
        Assert.Equal("dev.example.com", host.HostName);
        Assert.Equal("alice", host.User);
        Assert.Equal(2222, host.Port);
        // IdentityFile 累加,按出现顺序:先 Host * 里的,再是 dev 自己的
        Assert.Equal([$"{Home}/.ssh/id_default", $"{Home}/.ssh/id_dev"], host.IdentityFiles);
        Assert.True(host.IdentitiesOnly);
        Assert.Equal("bastion", host.ProxyJump);
        Assert.Equal(30, host.ServerAliveIntervalSeconds);
    }

    [Fact]
    public void Wildcards_and_negation_follow_openssh()
    {
        var config = SshConfig.Parse(Sample, home: Home);
        Assert.Equal("wildcard", config.Resolve("app.example.com").User);
        Assert.Equal(2200, config.Resolve("app.example.com").Port);
        // !secret:取反的模式匹配到就整条不生效
        Assert.Null(config.Resolve("secret").User);
        // web-* 和 dev 共用一个 Host 块
        Assert.Equal("dev.example.com", config.Resolve("web-1").HostName);
    }

    [Fact]
    public void The_first_value_wins_and_a_requested_user_overrides_the_file()
    {
        var config = SshConfig.Parse("Host a\n User first\n Port 1\nHost a\n User second\n Port 2\nHost *\n User star\n", home: Home);
        var host = config.Resolve("a");
        Assert.Equal("first", host.User);
        Assert.Equal(1, host.Port);
        Assert.Equal("explicit", config.Resolve("a", "explicit").User);
        Assert.Equal("star", config.Resolve("other").User);
    }

    [Fact]
    public void Syntax_variants_are_accepted()
    {
        var config = SshConfig.Parse("""
            host   spaced
            	hostname=eq.example.com
            	PORT = 2201
            	IdentityFile "~/my keys/id one"   # trailing comment
            	user "quoted user"

            # comment between
            HOST Mixed
              HOSTNAME MIXED.example.com
            """, home: Home);
        var spaced = config.Resolve("spaced");
        Assert.Equal("eq.example.com", spaced.HostName);
        Assert.Equal(2201, spaced.Port);
        Assert.Equal([$"{Home}/my keys/id one"], spaced.IdentityFiles);
        Assert.Equal("quoted user", spaced.User);
        Assert.Equal("MIXED.example.com", config.Resolve("Mixed").HostName);
        // Host 模式不区分大小写
        Assert.Equal("MIXED.example.com", config.Resolve("mixed").HostName);
    }

    [Fact]
    public void Tokens_in_identity_files_and_host_names_are_expanded()
    {
        var config = SshConfig.Parse("Host box\n HostName %h.internal\n User bob\n Port 2022\n IdentityFile ~/.ssh/%r@%h-%p\n IdentityFile %d/k_%n_%u\n", home: Home);
        var host = config.Resolve("box", localUser: "me");
        Assert.Equal("box.internal", host.HostName);
        Assert.Equal([$"{Home}/.ssh/bob@box.internal-2022", $"{Home}/k_box_me"], host.IdentityFiles);
    }

    [Fact]
    public void ProxyJump_none_and_identity_agent_none_are_understood()
    {
        var config = SshConfig.Parse("Host a\n ProxyJump none\n IdentityAgent none\nHost *\n ProxyJump jump\n", home: Home);
        var host = config.Resolve("a");
        Assert.Null(host.ProxyJump);
        Assert.True(host.IdentityAgentDisabled);
        Assert.Equal("jump", config.Resolve("b").ProxyJump);
    }

    [Fact]
    public void Invalid_numbers_fall_back_to_defaults_and_flags_are_parsed()
    {
        var config = SshConfig.Parse("Host a\n Port 99999\n ConnectTimeout 12\n PasswordAuthentication no\n PubkeyAuthentication yes\n KbdInteractiveAuthentication no\n ServerAliveCountMax 5\n", home: Home);
        var host = config.Resolve("a");
        Assert.Equal(22, host.Port);
        Assert.Equal(12, host.ConnectTimeoutSeconds);
        Assert.False(host.PasswordAuthentication);
        Assert.True(host.PubkeyAuthentication);
        Assert.False(host.KbdInteractiveAuthentication);
        Assert.Equal(5, host.ServerAliveCountMax);
        Assert.Equal("a", host.HostName);
    }

    [Fact]
    public void ProxyCommand_is_reported_but_not_executed()
    {
        var host = SshConfig.Parse("Host a\n ProxyCommand nc -X 5 -x proxy:1080 %h %p\n", home: Home).Resolve("a");
        Assert.NotNull(host.ProxyCommand);
        var error = Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.ConfigAlias("a"), SshConfig.Parse("Host a\n ProxyCommand nc %h %p\n", home: Home), "me"));
        Assert.Equal(EnvironmentErrorCode.Unsupported, error.Code);
    }

    [Fact]
    public void Match_blocks_support_all_host_originalhost_and_user_and_flag_the_rest()
    {
        var config = SshConfig.Parse("""
            Match host web*
              User webuser
            Match originalhost db
              Port 5432
            Match exec "test -f /x"
              User execuser
            Match all
              ServerAliveInterval 7
            """, home: Home);
        var web = config.Resolve("web1");
        Assert.Equal("webuser", web.User);
        Assert.Equal(7, web.ServerAliveIntervalSeconds);
        Assert.Equal(5432, config.Resolve("db").Port);
        // Match exec 不执行也不猜:整块忽略,并给出警告
        Assert.Contains("unsupported-match", web.Warnings);
        Assert.NotEqual("execuser", web.User);
    }

    [Fact]
    public void Include_reads_relative_and_globbed_files()
    {
        var files = new MemoryFiles(new Dictionary<string, string>
        {
            [$"{Home}/.ssh/config"] = "Include conf.d/*\nHost main\n HostName main.example.com\n",
            [$"{Home}/.ssh/conf.d/10-a"] = "Host a\n HostName a.example.com\n",
            [$"{Home}/.ssh/conf.d/20-b"] = "Host b\n HostName b.example.com\n User bee\n",
            [$"{Home}/.ssh/conf.d/sub/ignored"] = "Host c\n HostName c.example.com\n",
            [$"{Home}/shared"] = "Host s\n HostName s.example.com\n",
        });
        var config = SshConfig.Load(files);
        Assert.Equal("a.example.com", config.Resolve("a").HostName);
        Assert.Equal("bee", config.Resolve("b").User);
        Assert.Equal("main.example.com", config.Resolve("main").HostName);
        Assert.Equal("c", config.Resolve("c").HostName);
        Assert.Equal(["a", "b", "main"], config.ListHosts());

        // 绝对路径和 ~ 路径,以及自引用不会无限递归
        var loop = new MemoryFiles(new Dictionary<string, string>
        {
            [$"{Home}/.ssh/config"] = "Include ~/shared\nInclude config\n",
            [$"{Home}/shared"] = "Host s\n HostName s.example.com\n",
        });
        Assert.Equal("s.example.com", SshConfig.Load(loop).Resolve("s").HostName);
    }

    [Fact]
    public void Missing_config_is_an_empty_config()
    {
        var config = SshConfig.Load(new MemoryFiles([]));
        Assert.Empty(config.ListHosts());
        var host = config.Resolve("anything");
        Assert.Equal("anything", host.HostName);
        Assert.Equal(22, host.Port);
        Assert.Null(host.User);
    }

    [Fact]
    public void ListHosts_returns_only_concrete_aliases_for_import()
    {
        var config = SshConfig.Parse(Sample, home: Home);
        Assert.Equal(["dev", "bastion"], config.ListHosts());
    }

    [Theory]
    [InlineData("*", "anything", true)]
    [InlineData("web-*", "web-1", true)]
    [InlineData("web-?", "web-12", false)]
    [InlineData("*.example.com", "A.EXAMPLE.COM", true)]
    [InlineData("a*b*c", "aXXbYYc", true)]
    [InlineData("a*b*c", "aXXbYY", false)]
    [InlineData("", "", true)]
    [InlineData("?", "", false)]
    public void Glob_matches_star_and_question_mark_case_insensitively(string pattern, string text, bool expected) =>
        Assert.Equal(expected, SshConfig.Glob(pattern, text));
}

/// <summary>ProxyJump 解析与整条路线的解析:纯逻辑。</summary>
public class SshRouteTests
{
    private const string Home = "/home/me";

    private static SshConfig Config(string text) => SshConfig.Parse(text, home: Home);

    [Fact]
    public void ProxyJump_text_is_parsed()
    {
        Assert.Empty(SshRouteResolver.ParseProxyJump(null));
        Assert.Empty(SshRouteResolver.ParseProxyJump("none"));
        Assert.Equal([new SshJumpSpec(null, "a", null)], SshRouteResolver.ParseProxyJump("a"));
        Assert.Equal([new SshJumpSpec("u", "a", 2222), new SshJumpSpec(null, "b", null)], SshRouteResolver.ParseProxyJump("u@a:2222, b"));
        Assert.Equal([new SshJumpSpec("u", "::1", 2200)], SshRouteResolver.ParseProxyJump("u@[::1]:2200"));
        Assert.Equal([new SshJumpSpec("u", "host", 22)], SshRouteResolver.ParseProxyJump("ssh://u@host:22/"));
    }

    [Theory]
    [InlineData("u@a:0")]
    [InlineData("u@a:99999")]
    [InlineData("u@a:port")]
    [InlineData("@a")]
    [InlineData("-oProxyCommand=evil")]
    [InlineData("[::1")]
    public void Bad_ProxyJump_is_rejected(string text)
    {
        var error = Assert.Throws<EnvironmentException>(() => SshRouteResolver.ParseProxyJump(text));
        Assert.Equal(EnvironmentErrorCode.InvalidProfile, error.Code);
    }

    [Fact]
    public void A_manual_target_with_ProxyJump_chains_jump_hosts_in_order()
    {
        var route = SshRouteResolver.Resolve(new SshTarget.Manual("target", 2222, "deploy", "/keys/id", "j1@jump1:2200,jump2"), Config(""), "me");
        Assert.Collection(route,
            h => Assert.Equal(("jump1", 2200, "j1"), (h.HostName, h.Port, h.User)),
            h => Assert.Equal(("jump2", 22, "me"), (h.HostName, h.Port, h.User)),
            h => Assert.Equal(("target", 2222, "deploy"), (h.HostName, h.Port, h.User)));
        // 手填目标只有一把私钥:没有自己配置的跳板机沿用它
        Assert.All(route, h => Assert.Equal(["/keys/id"], h.IdentityFiles));
        Assert.Equal("[jump1]:2200", route[0].HostKeyName);
        Assert.Equal("jump2", route[1].HostKeyName);
        Assert.Equal("[target]:2222", route[2].HostKeyName);
    }

    [Fact]
    public void Jump_host_names_are_resolved_through_the_config()
    {
        var config = Config("""
            Host dev
              HostName dev.internal
              User alice
              ProxyJump bastion
            Host bastion
              HostName bastion.example.com
              User jump
              Port 2200
              IdentityFile ~/.ssh/id_bastion
            """);
        var route = SshRouteResolver.Resolve(new SshTarget.ConfigAlias("dev"), config, "me");
        Assert.Equal(2, route.Count);
        Assert.Equal(("bastion.example.com", 2200, "jump"), (route[0].HostName, route[0].Port, route[0].User));
        Assert.Equal([$"{Home}/.ssh/id_bastion"], route[0].IdentityFiles);
        Assert.Equal(("dev.internal", 22, "alice"), (route[1].HostName, route[1].Port, route[1].User));
        Assert.Equal("[bastion.example.com]:2200", route[0].HostKeyName);
    }

    [Fact]
    public void A_jump_hosts_own_ProxyJump_is_prepended()
    {
        var config = Config("""
            Host target
              ProxyJump mid
            Host mid
              HostName mid.example.com
              ProxyJump outer
            Host outer
              HostName outer.example.com
            """);
        var route = SshRouteResolver.Resolve(new SshTarget.ConfigAlias("target"), config, "me");
        Assert.Equal(["outer.example.com", "mid.example.com", "target"], route.Select(h => h.HostName));
    }

    [Fact]
    public void Explicit_user_and_port_in_ProxyJump_override_the_config()
    {
        var config = Config("Host bastion\n HostName b.example.com\n User cfg\n Port 1\nHost target\n ProxyJump over@bastion:9\n");
        var route = SshRouteResolver.Resolve(new SshTarget.ConfigAlias("target"), config, "me");
        Assert.Equal(("b.example.com", 9, "over"), (route[0].HostName, route[0].Port, route[0].User));
    }

    [Fact]
    public void Loops_and_runaway_chains_are_rejected()
    {
        var loop = Config("Host a\n ProxyJump b\nHost b\n ProxyJump a\n");
        Assert.Equal(EnvironmentErrorCode.InvalidProfile, Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.ConfigAlias("a"), loop, "me")).Code);
        var self = Config("Host a\n ProxyJump a\n");
        Assert.Equal(EnvironmentErrorCode.InvalidProfile, Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.ConfigAlias("a"), self, "me")).Code);
    }

    [Fact]
    public void An_identity_file_on_the_target_replaces_the_configured_ones()
    {
        var config = Config("Host a\n IdentityFile ~/.ssh/from_config\n");
        var route = SshRouteResolver.Resolve(new SshTarget.ConfigAlias("a", IdentityFile: "/explicit/key"), config, "me");
        Assert.Equal(["/explicit/key"], route[^1].IdentityFiles);
    }

    [Fact]
    public void HostKeyAlias_overrides_the_pinning_name()
    {
        var config = Config("Host a\n HostName 10.0.0.2\n Port 2222\n HostKeyAlias friendly\n");
        Assert.Equal("friendly", SshRouteResolver.Resolve(new SshTarget.ConfigAlias("a"), config, "me")[0].HostKeyName);
    }

    [Theory]
    [InlineData("-oProxyCommand=bad")]
    [InlineData("has space")]
    [InlineData("")]
    public void Option_injection_in_the_host_is_rejected(string host)
    {
        Assert.Equal(EnvironmentErrorCode.InvalidProfile, Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.ConfigAlias(host), Config(""), "me")).Code);
        Assert.Equal(EnvironmentErrorCode.InvalidProfile, Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.Manual(host, 22, "u"), Config(""), "me")).Code);
    }

    [Fact]
    public void Manual_targets_are_validated()
    {
        Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.Manual("h", 0, "u"), Config(""), "me"));
        Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.Manual("h", 70000, "u"), Config(""), "me"));
        Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.Manual("h", 22, ""), Config(""), "me"));
        Assert.Throws<EnvironmentException>(() => SshRouteResolver.Resolve(new SshTarget.Manual("h", 22, "u\nx"), Config(""), "me"));
    }

    [Fact]
    public void Known_hosts_names_match_openssh()
    {
        Assert.Equal("host", SshHop.KnownHostsName("host", 22));
        Assert.Equal("[host]:2222", SshHop.KnownHostsName("host", 2222));
    }
}
