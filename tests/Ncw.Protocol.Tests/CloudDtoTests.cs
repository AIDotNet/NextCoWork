using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 云端 Agent 的客户端可见类型(<c>cloud.*</c>、<c>git.credentials.*</c>、<c>projects.add</c> 追加的克隆字段)的线上形状:往返、方法表声明、冻结向量(<see cref="GoldenV1Cloud"/>)。
/// 这一批只新增方法 / 载荷类型和 <c>ProjectsAddParams</c> 的带默认值的末尾字段,没改帧格式也没改任何已冻结类型的已有键,所以仍是协议 v1。
/// </summary>
public class CloudDtoTests
{
    private static readonly CloudQuotaDto Quota = new(100, 3000, 2_000_000, 40_000_000, 2, 360_000, 10_000_000_000, false);

    public static IEnumerable<(string Name, object Value)> Samples() =>
    [
        ("cloud.status", new CloudStatus(
            CloudStates.Awake, 1_700_000_000_000, true, 1_700_000_001_000, Quota,
            new CloudUsageDto(3, 40, 12_000, 900_000, 1, 7_200, 123_456_789, 1_699_920_000_000, 1_698_796_800_000),
            CloudErrorKeys.QuotaExceeded, "dailyRuns", [new CloudInterruptedRunDto("run-1", "sess-1", 1_699_999_000_000)], "acct-1")),
        ("cloud.status.unlimited", new CloudStatus(
            CloudStates.Awake, 1, false, null, new CloudQuotaDto(null, null, null, null, null, null, null, false),
            new CloudUsageDto(0, 0, 0, 0, 0, 0, null, 0, 0), null, null, [], null)),
        ("cloud.clearInterrupted.params", new CloudClearInterruptedParams(["run-1", "run-2"])),
        ("git.credentials.set.params", new GitCredentialSetParams("github.com", "ghp_example", "x-access-token")),
        ("git.credentials.set.paramsNoUser", new GitCredentialSetParams("git.example.com:8443", "tok")),
        ("git.credentials.remove.params", new GitCredentialRemoveParams("github.com")),
        ("git.credentials.result", new GitCredentialsResult([new GitCredentialDto("github.com", "x-access-token")])),
        ("projects.add.clone", new ProjectsAddParams("work/app", "app", null, "https://github.com/org/app.git", "main")),
        ("projects.add.cloneDefaultBranch", new ProjectsAddParams("work/app", null, null, "https://github.com/org/app.git")),
    ];

    public static TheoryData<string> Names() => [.. Samples().Select(static s => s.Name)];

    public static string HexOf(object value) => Convert.ToHexStringLower(Encode(value));

    private static byte[] Encode(object value) => value switch
    {
        CloudStatus v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<CloudStatus>()),
        CloudClearInterruptedParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<CloudClearInterruptedParams>()),
        GitCredentialSetParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<GitCredentialSetParams>()),
        GitCredentialRemoveParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<GitCredentialRemoveParams>()),
        GitCredentialsResult v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<GitCredentialsResult>()),
        ProjectsAddParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<ProjectsAddParams>()),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    private static byte[] ReEncode(object value, byte[] bytes) => value switch
    {
        CloudStatus => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<CloudStatus>())),
        CloudClearInterruptedParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<CloudClearInterruptedParams>())),
        GitCredentialSetParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<GitCredentialSetParams>())),
        GitCredentialRemoveParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<GitCredentialRemoveParams>())),
        GitCredentialsResult => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<GitCredentialsResult>())),
        ProjectsAddParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<ProjectsAddParams>())),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void Every_sample_round_trips_to_the_same_bytes(string name)
    {
        var value = Samples().Single(s => s.Name == name).Value;
        var bytes = Encode(value);

        Assert.Equal(Convert.ToHexStringLower(bytes), Convert.ToHexStringLower(ReEncode(value, bytes)));
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_vectors_v1_cloud(string name) =>
        Assert.Equal(GoldenV1Cloud.Of(name), Convert.ToHexStringLower(Encode(Samples().Single(s => s.Name == name).Value)));

    [Fact]
    public void A_projects_add_payload_from_before_the_clone_fields_still_decodes_with_null_defaults()
    {
        // 加 CloneUrl / CloneBranch 之前的控制端发来的 projects.add(含 EnvironmentId):必须仍能解码
        var old = FrameCodec.DecodePayload(Convert.FromHexString(GoldenV1Cloud.ProjectsAddParamsBeforeClone), PayloadShapes.GetShape<ProjectsAddParams>());

        Assert.Equal(("/srv/app", "app", "7c1f"), (old.Path, old.Name, old.EnvironmentId));
        Assert.Null(old.CloneUrl);
        Assert.Null(old.CloneBranch);
    }

    [Fact]
    public void The_method_table_declares_kind_role_capability_and_transport()
    {
        Assert.Equal((MethodKind.Query, Role.Viewer, Capabilities.Cloud, Transports.Any), (CloudMethods.GetStatus.Kind, CloudMethods.GetStatus.MinRole, CloudMethods.GetStatus.Capability, CloudMethods.GetStatus.Allowed));
        Assert.Equal((MethodKind.Command, Role.Member), (CloudMethods.ClearInterrupted.Kind, CloudMethods.ClearInterrupted.MinRole));
        // Git 凭据只有 owner:令牌是秘密,member 不能读、写、删
        Assert.All(GitCredentialsMethods.All, m => Assert.Equal(Role.Owner, m.MinRole));
        Assert.All(GitCredentialsMethods.All, m => Assert.Equal(Capabilities.GitCredentials, m.Capability));
        Assert.Equal(MethodKind.Command, GitCredentialsMethods.Set.Kind); // command 必带幂等键
        Assert.Equal(MethodKind.Query, GitCredentialsMethods.List.Kind);
    }

    [Fact]
    public void The_git_credential_listing_type_has_no_field_that_could_carry_a_token()
    {
        // 令牌只出现在 set 的请求里;任何会回到控制端的类型都不能有 token 字段
        NoSecretFields(typeof(GitCredentialDto));
        NoSecretFields(typeof(GitCredentialsResult));
    }

    private static void NoSecretFields([System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties)] Type type) =>
        Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase));
}
