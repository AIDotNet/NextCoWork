using System.Net;
using Microsoft.Data.Sqlite;
using Ncw.Agent.Environments;
using Ncw.Agent.Host.Conversation;
using Ncw.Agent.Host.Services;
using Ncw.Agent.Kernel.Session;
using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Services.Settings;
using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;

namespace Ncw.Agent.Host.Tests;

/// <summary>
/// 设置接线:原先是出厂值的几项(指令文件目录、各辅助模型、空闲超时、账号轮换、故障转移、目标检查间隔、代理)现在读设置页的值,<b>每次现读</b>。
/// 这里直接测装配类(Host 对测试项目开放内部类型):出厂值与 2.x 一致;改完设置,下一次读到的就是新值,没有任何缓存。
/// </summary>
public sealed class SettingsWiringTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ncwwire-{Guid.NewGuid():N}");
    private readonly AgentDatabase _db;
    private readonly SettingsService _settings;
    private readonly LocalEnvironment _env = new("device-wire");

    public SettingsWiringTests()
    {
        _db = AgentDatabase.Open(_root);
        _settings = new SettingsService(new SettingsRepository(_db));
    }

    public void Dispose()
    {
        _env.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private StoredSessionSettings Session() => new(_settings, () => new GlobalInstructions(_env, EnvPath.Parse(_root, _env.Facts.PathStyle)));

    // ───────────────────────── 会话设置快照 ─────────────────────────

    [Fact]
    public void The_run_snapshot_has_the_factory_values_and_the_global_instructions_directory_is_the_data_root()
    {
        var s = Session().Read();

        Assert.True(s.AutoCompact);
        Assert.Null(s.MaxOutputTokens);        // 出厂值 = 不覆盖
        Assert.Null(s.Personalization);
        Assert.Null(s.Compaction);
        Assert.NotNull(s.GlobalInstructions);  // 全局指令读设备本地的数据根(04 §2)
        Assert.Equal(EnvPath.Parse(_root, _env.Facts.PathStyle).Value, s.GlobalInstructions!.Root.Value);
        Assert.Null(s.ImportedInstructions);
    }

    [Fact]
    public void Writing_a_setting_changes_the_next_run_snapshot_with_no_cache_in_between()
    {
        var session = Session();
        Assert.True(session.Read().AutoCompact);

        _settings.Update(new DeviceSettingsPatch
        {
            AutoCompact = false,
            MaxOutputTokens = 12_000,
            Personalization = new PersonalizationPatch("Ada", "engineer", "be brief"),
            Compact = new ModelPick("cheap", "p9"),
            CompactThinking = "low",
        });

        var s = session.Read();
        Assert.False(s.AutoCompact);
        Assert.Equal(12_000, s.MaxOutputTokens);
        Assert.Equal(("Ada", "engineer", "be brief"), (s.Personalization!.Name, s.Personalization.Background, s.Personalization.Instructions));
        Assert.Equal(("cheap", "p9"), (s.Compaction!.Model, s.Compaction.ModelProviderId));
        Assert.Equal(CompactThinkingSetting.Of(ThinkingLevel.Low), s.Compaction.Thinking);

        _settings.Update(new DeviceSettingsPatch { Compact = ModelPick.None, CompactThinking = "inherit" });
        Assert.Null(session.Read().Compaction); // 清掉 = 跟随会话模型
    }

    // ───────────────────────── 子代理 / 目标 ─────────────────────────

    [Fact]
    public void Subagent_settings_are_read_from_the_settings_page_each_time()
    {
        var source = new StoredSubagentSettings(_settings);
        var factory = source.Current();
        Assert.Null(factory.Model);
        Assert.Equal((4, 4), (factory.PerSessionLimit, factory.GlobalLimit));

        _settings.Update(new DeviceSettingsPatch { Subagent = new SubagentSettingsPatch(new ModelPick("haiku", "p2"), "high", 2, 0) });

        var s = source.Current();
        Assert.Equal(("haiku", "p2"), (s.Model!.Alias, s.Model.ProviderId));
        Assert.Equal((2, 0), (s.PerSessionLimit, s.GlobalLimit)); // 全局 0 = 禁用,原样给值
        Assert.Equal(Ncw.Agent.Kernel.Subagents.SubagentThinking.Of(ThinkingLevel.High), s.Thinking);
    }

    [Fact]
    public void Goal_settings_the_evaluator_model_the_proposal_policy_and_the_checkin_interval_are_read_each_time()
    {
        var goals = new StoredGoalSettings(_settings);
        Assert.Null(goals.EvaluatorModel());
        Assert.Equal(Ncw.Agent.Extensions.Planning.Goals.GoalProposalPolicy.Auto, goals.ProposalPolicy());
        Assert.Equal(TimeSpan.FromMinutes(30), goals.CheckinInterval());

        _settings.Update(new DeviceSettingsPatch { GoalEvaluator = new ModelPick("judge", "p3"), ModelProposedGoals = "alwaysAsk", GoalCheckinMinutes = 5 });

        Assert.Equal(new ModelRef("p3", "judge"), goals.EvaluatorModel());
        Assert.Equal(Ncw.Agent.Extensions.Planning.Goals.GoalProposalPolicy.AlwaysAsk, goals.ProposalPolicy());
        Assert.Equal(TimeSpan.FromMinutes(5), goals.CheckinInterval());
        _settings.Update(new DeviceSettingsPatch { ModelProposedGoals = "disabled" });
        Assert.Equal(Ncw.Agent.Extensions.Planning.Goals.GoalProposalPolicy.Disabled, goals.ProposalPolicy());
    }

    // ───────────────────────── 路由 ─────────────────────────

    [Fact]
    public void The_router_idle_timeout_follows_the_setting_clamped_to_60_to_3600_seconds()
    {
        var idle = LlmRouterOptions.IdleTimeoutFromSettings(() => _settings.Current().UpstreamIdleTimeoutSeconds);
        Assert.Equal(TimeSpan.FromSeconds(600), idle());

        _settings.Update(new DeviceSettingsPatch { UpstreamIdleTimeoutSeconds = 1800 });
        Assert.Equal(TimeSpan.FromSeconds(1800), idle());
    }

    [Fact]
    public void Account_rotation_is_on_and_failover_is_off_by_default_and_both_are_two_separate_switches()
    {
        Assert.Equal((true, false), (_settings.Current().AccountRotation, _settings.Current().Failover));

        _settings.Update(new DeviceSettingsPatch { Failover = true });
        Assert.Equal((true, true), (_settings.Current().AccountRotation, _settings.Current().Failover));
        _settings.Update(new DeviceSettingsPatch { AccountRotation = false });
        Assert.Equal((false, true), (_settings.Current().AccountRotation, _settings.Current().Failover));
    }

    // ───────────────────────── 代理 ─────────────────────────

    private SettingsProxy Proxy(out ProxyPasswordStore password)
    {
        password = new ProxyPasswordStore(new CredentialStore(_db, _root, NoLegacySecrets.Instance));
        return new SettingsProxy(_settings, password);
    }

    [Fact]
    public void A_disabled_proxy_means_direct_connections_and_a_manual_one_applies_per_request_with_the_built_in_direct_list()
    {
        var proxy = Proxy(out _);
        var target = new Uri("https://api.example.com/v1");
        Assert.Null(proxy.GetProxy(target));
        Assert.True(proxy.IsBypassed(target));

        _settings.Update(new DeviceSettingsPatch { Proxy = new ProxySettingsPatch(Enabled: true, Mode: "manual", Scheme: "socks5", Host: "proxy.corp", Port: 1080) });

        Assert.Equal(new Uri("socks5://proxy.corp:1080"), proxy.GetProxy(target));
        Assert.False(proxy.IsBypassed(target));
        // 内置直连:回环、私有网段、局域网单标签主机名(走代理反而会坏)
        foreach (var local in new[] { "http://localhost:8080", "http://127.0.0.1/x", "http://192.168.1.5/x", "http://10.1.2.3/x", "http://172.20.0.1/x", "http://[::1]/x", "http://nas/x" })
        {
            Assert.True(proxy.IsBypassed(new Uri(local)), local);
            Assert.Null(proxy.GetProxy(new Uri(local)));
        }

        Assert.False(proxy.IsBypassed(new Uri("http://172.32.0.1/x"))); // 172.16/12 之外
    }

    [Fact]
    public void The_users_direct_list_supports_hosts_wildcards_and_cidr_ranges_and_changes_apply_to_the_next_request()
    {
        var proxy = Proxy(out _);
        _settings.Update(new DeviceSettingsPatch { Proxy = new ProxySettingsPatch(Enabled: true, Mode: "manual", Host: "p", Port: 3128, Bypass: "internal.example.com\n*.corp.example.com\n203.0.113.0/24") });

        Assert.True(proxy.IsBypassed(new Uri("https://internal.example.com/")));
        Assert.True(proxy.IsBypassed(new Uri("https://git.corp.example.com/")));
        Assert.True(proxy.IsBypassed(new Uri("https://corp.example.com/")));
        Assert.True(proxy.IsBypassed(new Uri("https://203.0.113.77/")));
        Assert.False(proxy.IsBypassed(new Uri("https://api.example.com/")));
        Assert.False(proxy.IsBypassed(new Uri("https://203.0.114.1/")));

        _settings.Update(new DeviceSettingsPatch { Proxy = new ProxySettingsPatch(Bypass: "") }); // 清空白名单:下一个请求就走代理
        Assert.False(proxy.IsBypassed(new Uri("https://internal.example.com/")));
    }

    [Fact]
    public void The_proxy_credentials_come_from_the_encrypted_password_store_only_when_authentication_is_on()
    {
        var proxy = Proxy(out var password);
        _settings.Update(new DeviceSettingsPatch { Proxy = new ProxySettingsPatch(Enabled: true, Mode: "manual", Host: "p", Port: 3128) });
        Assert.Null(proxy.Credentials);

        password.Set("s3cret");
        _settings.Update(new DeviceSettingsPatch { Proxy = new ProxySettingsPatch(AuthEnabled: true, AuthUser: "ada") });
        proxy.Invalidate();

        var credential = proxy.Credentials!.GetCredential(new Uri("http://p:3128"), "Basic")!;
        Assert.Equal(("ada", "s3cret"), (credential.UserName, credential.Password));
        Assert.True(password.Has);

        password.Set("");
        proxy.Invalidate();
        Assert.Null(proxy.Credentials);
        Assert.False(password.Has);
    }
}
