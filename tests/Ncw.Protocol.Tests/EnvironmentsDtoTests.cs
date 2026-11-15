using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 执行环境(SSH 多设备执行)的线上形状:每个类型往返、方法表声明齐全(种类 / 角色 / 能力)、主题、新增的交互类型、项目的环境标识。
/// 冻结的向量在 <see cref="GoldenV1Environments"/>(只增向量,不改已冻结的旧向量)。
/// </summary>
public class EnvironmentsDtoTests
{
    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    private static T RoundTrip<T>(T value) => FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()), PayloadShapes.GetShape<T>());

    private static readonly EnvironmentFactsDto Facts = new("linux", "arm64", "build-box", "dev");

    private static EnvironmentDto Local() => new("local", "Local", EnvironmentKindNames.Local, EnvironmentStateNames.Ready, 0, true, true, 0, Facts: new EnvironmentFactsDto("macos", "arm64", "laptop", "me"));

    private static EnvironmentDto Ready() => new(
        "7c1f", "build-box", EnvironmentKindNames.Ssh, EnvironmentStateNames.Ready, 2, false, true, 3,
        LatencyMs: 12,
        Target: new SshTargetDto("manual", "10.0.0.5", 2222, "dev", true, "jump@gate:22", false),
        AuthMethod: "key", Platform: "linux", Proxy: new SshProxyDto("socks5", "127.0.0.1", 1080, "u", true), HasSavedPassword: true, Facts: Facts);

    private static EnvironmentDto Failed() => new(
        "7c1f", "build-box", EnvironmentKindNames.Ssh, EnvironmentStateNames.Failed, 0, false, true, 3,
        Error: new EnvironmentErrorDto(EnvironmentErrorKeys.HostKeyChanged,
            new HostKeyMismatchDto("[10.0.0.5]:2222", "ssh-ed25519", "presentedFp", [new PinnedKeyDto("ssh-ed25519", "pinnedFp")])),
        Target: new SshTargetDto("config", "prod", null, null, false, null, true), AuthMethod: "auto", Platform: "auto");

    private static EnvironmentInput Input() => new(
        "build-box", new SshTargetInput("manual", "10.0.0.5", 2222, "dev", "/home/me/.ssh/id_ed25519", null, "jump@gate"), "key", "linux", true, new SshProxyInput("socks5", "127.0.0.1", 1080, "u"));

    [Fact]
    public void Every_environment_type_round_trips()
    {
        Assert.Equal(Hex(Ready()), Hex(RoundTrip(Ready())));
        var failed = RoundTrip(Failed());
        Assert.Equal(EnvironmentErrorKeys.HostKeyChanged, failed.Error!.Key);
        Assert.Equal(("presentedFp", "pinnedFp"), (failed.Error.HostKey!.Presented, failed.Error.HostKey.Pinned[0].Fingerprint));
        Assert.Null(RoundTrip(Local()).Target);

        var state = RoundTrip(new EnvironmentsTopicState(9, "local", [Local(), Ready()]));
        Assert.Equal((9L, "local", 2), (state.Rev, state.DefaultId, state.Environments.Length));
        Assert.Equal(("build-box", true, 12L), (state.Environments[1].Name, state.Environments[1].HasSavedPassword, state.Environments[1].LatencyMs));

        var create = RoundTrip(new EnvironmentCreateParams(Input(), new EnvironmentSecretsInput("pw", "phrase", null)));
        Assert.Equal(("pw", "phrase", null), (create.Secrets!.Password, create.Secrets.KeyPassphrase, create.Secrets.ProxyPassword));
        Assert.Equal(("/home/me/.ssh/id_ed25519", "jump@gate"), (create.Profile.Target.IdentityFile, create.Profile.Target.ProxyJump));
        var update = RoundTrip(new EnvironmentUpdateParams("7c1f", 3, Input(), null, ConfirmTargetChange: true));
        Assert.True(update.ConfirmTargetChange);
        Assert.Null(update.Secrets);

        var ok = RoundTrip(new EnvironmentTestResult(true, null, 15, Facts));
        Assert.Equal((true, 15L), (ok.Ok, ok.LatencyMs));
        Assert.Equal(EnvironmentErrorKeys.Authentication, RoundTrip(new EnvironmentTestResult(false, new EnvironmentErrorDto(EnvironmentErrorKeys.Authentication))).Error!.Key);

        var directory = RoundTrip(new RemoteDirectoryDto("/home/dev", "/home", [new RemoteCrumbDto("/", "/"), new RemoteCrumbDto("home", "/home")], ["/"], [new RemoteDirEntryDto("src", "/home/dev/src")], false, Facts));
        Assert.Equal(("/home/dev", "/home", 1), (directory.Path, directory.Parent, directory.Entries.Length));

        var import = RoundTrip(new SshConfigImportResult([new SshConfigCandidateDto("prod", "10.0.0.5", 22, "dev", true, null, true, ["environments.import.unsupported-match"])], []));
        Assert.Equal(("prod", true), (import.Candidates[0].Alias, import.Candidates[0].HasIdentityFile));
        Assert.Equal("SHA256fp", RoundTrip(new KnownHostsResult([new KnownHostDto("[h]:2", "ssh-ed25519", "SHA256fp")])).Hosts[0].Fingerprint);
        Assert.Equal("h", RoundTrip(new ForgetHostKeyParams("h")).HostName);
        Assert.Equal("x", RoundTrip(new EnvironmentBrowseParams("x")).EnvironmentId);
    }

    [Fact]
    public void The_method_table_declares_kind_role_and_capability_for_every_method()
    {
        Assert.Equal(13, EnvironmentsMethods.All.Count);
        Assert.Equal(EnvironmentsMethods.All.Count, EnvironmentsMethods.All.Select(m => m.Name).Distinct().Count());
        Assert.All(EnvironmentsMethods.All, m =>
        {
            Assert.Equal(Capabilities.Environments, m.Capability);
            Assert.Equal(Transports.Any, m.Allowed);
            Assert.StartsWith("environments.", m.Name);
        });

        // 读取 member 起(清单里有内网主机名和用户名,属于设备配置,viewer 不给)
        MethodDef[] memberQueries = [EnvironmentsMethods.List, EnvironmentsMethods.Status, EnvironmentsMethods.Browse];
        Assert.All(memberQueries, m => Assert.Equal((MethodKind.Query, Role.Member), (m.Kind, m.MinRole)));
        // 连接 / 断开 member 起,是 command(必带幂等键)
        Assert.All(new MethodDef[] { EnvironmentsMethods.Connect, EnvironmentsMethods.Disconnect }, m => Assert.Equal((MethodKind.Command, Role.Member), (m.Kind, m.MinRole)));
        // 配置、默认环境、试连接决定设备会去连谁:owner
        MethodDef[] ownerCommands = [EnvironmentsMethods.Create, EnvironmentsMethods.Update, EnvironmentsMethods.Remove, EnvironmentsMethods.SetDefault, EnvironmentsMethods.Test, EnvironmentsMethods.ForgetHostKey];
        Assert.All(ownerCommands, m => Assert.Equal((MethodKind.Command, Role.Owner), (m.Kind, m.MinRole)));
        // 读设备上的 ~/.ssh/config、看已知主机:owner
        Assert.All(new MethodDef[] { EnvironmentsMethods.ImportSshConfig, EnvironmentsMethods.KnownHosts }, m => Assert.Equal((MethodKind.Query, Role.Owner), (m.Kind, m.MinRole)));
    }

    [Fact]
    public void The_topic_is_member_only_and_named_environments()
    {
        Assert.Equal("environments", EnvironmentsMethods.Topic.Name);
        Assert.Equal(Role.Member, EnvironmentsMethods.Topic.MinRole);
        Assert.Equal("environments.changed", EnvironmentsEvents.Changed);
    }

    [Fact]
    public void Projects_carry_their_environment_and_old_payloads_still_decode()
    {
        var remote = RoundTrip(new ProjectDto("w1", "build-box: app", "/srv/app", "ssh", "build-box", 1_700_000_000_000, "7c1f"));
        Assert.Equal(("ssh", "7c1f"), (remote.Environment, remote.EnvironmentId));
        Assert.Null(RoundTrip(new ProjectDto("w1", "app", "/a", "local", null, 1)).EnvironmentId);
        Assert.Equal("7c1f", RoundTrip(new ProjectsAddParams("/srv/app", null, "7c1f")).EnvironmentId);

        // 旧控制端 / 旧设备的载荷(没有 EnvironmentId 这个键)照常解码:带默认值的构造函数参数可以省略
        var oldAdd = FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1Environments.ProjectsAddParamsWithoutEnvironment), PayloadShapes.GetShape<ProjectsAddParams>());
        Assert.Equal(("/a", null, null), (oldAdd.Path, oldAdd.Name, oldAdd.EnvironmentId));
        var oldProject = FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1Environments.ProjectDtoWithoutEnvironment), PayloadShapes.GetShape<ProjectDto>());
        Assert.Equal(("w1", null), (oldProject.WorkspaceId, oldProject.EnvironmentId));
    }

    [Fact]
    public void The_ssh_interactions_round_trip_through_the_interaction_union()
    {
        var hostKey = RoundTrip<InteractionDto>(new SshHostKeyInteractionDto
        {
            Id = "i1", RunId = "env:7c1f:x", CreatedAt = 5, Version = 9,
            Env = new EnvRefDto("dev1", "7c1f", "build-box", "ssh"), HostName = "[10.0.0.5]:2222", Algorithm = "ssh-ed25519", Fingerprint = "fp",
        });
        var h = Assert.IsType<SshHostKeyInteractionDto>(hostKey);
        Assert.Equal(("ssh", "7c1f", "[10.0.0.5]:2222", "fp"), (h.Env.Type, h.Env.Environment, h.HostName, h.Fingerprint));

        var credential = Assert.IsType<SshCredentialInteractionDto>(RoundTrip<InteractionDto>(new SshCredentialInteractionDto
        {
            Id = "i2", RunId = "env:7c1f:y", Version = 10,
            Env = new EnvRefDto("dev1", "7c1f", "build-box", "ssh"), PromptKind = "keyboardInteractive", HostName = "10.0.0.5", UserName = "dev",
            Instruction = "two-step", Prompts = [new SshPromptDto("Code: ", true)], PreviousRejected = true, CanRemember = false, HasSaved = true,
        }));
        Assert.Equal(("keyboardInteractive", "Code: ", true, true), (credential.PromptKind, credential.Prompts[0].Text, credential.Prompts[0].Echo, credential.PreviousRejected));

        Assert.True(Assert.IsType<SshHostKeyResponseDto>(RoundTrip<InteractionResponseDto>(new SshHostKeyResponseDto(true))).Trust);
        var answer = Assert.IsType<SshCredentialResponseDto>(RoundTrip<InteractionResponseDto>(new SshCredentialResponseDto(["pw"], true)));
        Assert.Equal(["pw"], answer.Answers!);
        Assert.True(answer.Remember);
        Assert.Null(Assert.IsType<SshCredentialResponseDto>(RoundTrip<InteractionResponseDto>(new SshCredentialResponseDto(null))).Answers);
    }

    [Fact]
    public void Credentials_never_print_through_to_string()
    {
        // 自动生成的 ToString 会打出每个属性:凭据不能经日志、异常消息、断言输出带出去
        var secrets = new EnvironmentSecretsInput("hunter2", "phrase2", "proxy2");
        var text = new EnvironmentCreateParams(Input(), secrets).ToString() + new EnvironmentUpdateParams("i", 1, Input(), secrets) + new EnvironmentTestParams(Input(), null, secrets)
            + new SshCredentialResponseDto(["hunter2"]) + secrets;
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("phrase2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_response_or_topic_type_has_a_place_for_a_credential_or_a_key_path()
    {
        // 响应 / 主题 / 事件里只有「有没有」:这些类型的属性名里不能出现 Password / Passphrase / Secret / PrivateKey / IdentityFile(Has* 除外)
        PolyType.ITypeShape[] outbound =
        [
            PayloadShapes.GetShape<EnvironmentDto>(), PayloadShapes.GetShape<EnvironmentsTopicState>(), PayloadShapes.GetShape<SshTargetDto>(), PayloadShapes.GetShape<SshProxyDto>(),
            PayloadShapes.GetShape<EnvironmentErrorDto>(), PayloadShapes.GetShape<HostKeyMismatchDto>(), PayloadShapes.GetShape<EnvironmentTestResult>(),
            PayloadShapes.GetShape<RemoteDirectoryDto>(), PayloadShapes.GetShape<SshConfigCandidateDto>(), PayloadShapes.GetShape<SshConfigImportResult>(),
            PayloadShapes.GetShape<KnownHostsResult>(), PayloadShapes.GetShape<SshHostKeyInteractionDto>(), PayloadShapes.GetShape<SshCredentialInteractionDto>(),
            PayloadShapes.GetShape<EnvironmentFactsDto>(),
        ];
        var names = outbound.OfType<PolyType.Abstractions.IObjectTypeShape>().SelectMany(shape => shape.Properties.Select(p => p.Name)).ToList();
        Assert.Equal(outbound.Length, outbound.OfType<PolyType.Abstractions.IObjectTypeShape>().Count());
        Assert.All(names, name =>
        {
            Assert.DoesNotContain("Secret", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("PrivateKey", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Passphrase", name, StringComparison.OrdinalIgnoreCase);
            if (name.Contains("Password", StringComparison.OrdinalIgnoreCase) || name.Contains("IdentityFile", StringComparison.OrdinalIgnoreCase) || name.Contains("ConfigFile", StringComparison.OrdinalIgnoreCase))
            {
                Assert.StartsWith("Has", name);
            }
        });
    }

    [Fact]
    public void Golden_vectors_v1_environments()
    {
        Assert.Equal(GoldenV1Environments.Local, Hex(Local()));
        Assert.Equal(GoldenV1Environments.Ready, Hex(Ready()));
        Assert.Equal(GoldenV1Environments.Failed, Hex(Failed()));
        Assert.Equal(GoldenV1Environments.TopicState, Hex(new EnvironmentsTopicState(9, "local", [Local(), Ready()])));
        Assert.Equal(GoldenV1Environments.CreateParams, Hex(new EnvironmentCreateParams(Input(), new EnvironmentSecretsInput("pw", "phrase", null))));
        Assert.Equal(GoldenV1Environments.UpdateParams, Hex(new EnvironmentUpdateParams("7c1f", 3, Input(), null, true)));
        Assert.Equal(GoldenV1Environments.TestResultOk, Hex(new EnvironmentTestResult(true, null, 15, Facts)));
        Assert.Equal(GoldenV1Environments.TestResultFailed, Hex(new EnvironmentTestResult(false, new EnvironmentErrorDto(EnvironmentErrorKeys.Authentication))));
        Assert.Equal(GoldenV1Environments.Directory, Hex(new RemoteDirectoryDto("/home/dev", "/home", [new RemoteCrumbDto("/", "/"), new RemoteCrumbDto("home", "/home")], ["/"], [new RemoteDirEntryDto("src", "/home/dev/src")], false, Facts)));
        Assert.Equal(GoldenV1Environments.Import, Hex(new SshConfigImportResult([new SshConfigCandidateDto("prod", "10.0.0.5", 22, "dev", true, null, true, ["environments.import.unsupported-match"])], [])));
        Assert.Equal(GoldenV1Environments.KnownHosts, Hex(new KnownHostsResult([new KnownHostDto("[h]:2", "ssh-ed25519", "SHA256fp")])));
        Assert.Equal(GoldenV1Environments.Project, Hex(new ProjectDto("w1", "build-box: app", "/srv/app", "ssh", "build-box", 1_700_000_000_000, "7c1f")));
        Assert.Equal(GoldenV1Environments.HostKeyInteraction, Hex<InteractionDto>(new SshHostKeyInteractionDto
        {
            Id = "i1", RunId = "env:7c1f:x", CreatedAt = 5, Version = 9,
            Env = new EnvRefDto("dev1", "7c1f", "build-box", "ssh"), HostName = "[10.0.0.5]:2222", Algorithm = "ssh-ed25519", Fingerprint = "fp",
        }));
        Assert.Equal(GoldenV1Environments.CredentialInteraction, Hex<InteractionDto>(new SshCredentialInteractionDto
        {
            Id = "i2", RunId = "env:7c1f:y", Version = 10,
            Env = new EnvRefDto("dev1", "7c1f", "build-box", "ssh"), PromptKind = "keyboardInteractive", HostName = "10.0.0.5", UserName = "dev",
            Instruction = "two-step", Prompts = [new SshPromptDto("Code: ", true)], PreviousRejected = true, CanRemember = false, HasSaved = true,
        }));
        Assert.Equal(GoldenV1Environments.HostKeyResponse, Hex<InteractionResponseDto>(new SshHostKeyResponseDto(true)));
        Assert.Equal(GoldenV1Environments.CredentialResponse, Hex<InteractionResponseDto>(new SshCredentialResponseDto(["pw"], true)));
    }
}
