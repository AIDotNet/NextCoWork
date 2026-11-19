using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 设置 · 数据 · 回退到 2.x 的线上形状:载荷往返、方法表声明(种类、角色、承载)、冻结向量(<see cref="GoldenV1DataRollback"/>)。
/// 这一批只新增方法 / 载荷类型和 <c>data</c> 主题的一个状态值(<see cref="DataStates.RolledBack"/>),没改帧格式也没改任何已冻结类型的键,所以仍是协议 v1。
/// </summary>
public class DataRollbackDtoTests
{
    public static IEnumerable<(string Name, object Value)> Samples() =>
    [
        ("data.rollbackInfo.available", new DataRollbackInfo(true, 1_700_000_000_000, false)),
        ("data.rollbackInfo.none", new DataRollbackInfo(false, null, false)),
        ("data.requestRollback.params", new DataRollbackParams(true)),
    ];

    public static TheoryData<string> Names() => [.. Samples().Select(static s => s.Name)];

    private static byte[] Encode(object value) => value switch
    {
        DataRollbackInfo v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<DataRollbackInfo>()),
        DataRollbackParams v => FrameCodec.EncodePayload(v, PayloadShapes.GetShape<DataRollbackParams>()),
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    private static byte[] ReEncode(object value, byte[] bytes) => value switch
    {
        DataRollbackInfo => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<DataRollbackInfo>())),
        DataRollbackParams => Encode(FrameCodec.DecodePayload(bytes, PayloadShapes.GetShape<DataRollbackParams>())),
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
    public void Golden_vectors_v1_data_rollback(string name) =>
        Assert.Equal(GoldenV1DataRollback.Of(name), Convert.ToHexStringLower(Encode(Samples().Single(s => s.Name == name).Value)));

    [Fact]
    public void Rollback_methods_are_local_owner_only_and_the_writes_are_commands()
    {
        Assert.Equal((MethodKind.Query, Role.Owner, Transports.Local), (DeviceMethods.DataRollbackInfo.Kind, DeviceMethods.DataRollbackInfo.MinRole, DeviceMethods.DataRollbackInfo.Allowed));
        // command:路由要求幂等键;远程 owner 不能替这台设备决定回退(会让 Agent 退出、数据换回 2.x)
        Assert.Equal((MethodKind.Command, Role.Owner, Transports.Local), (DeviceMethods.DataRequestRollback.Kind, DeviceMethods.DataRequestRollback.MinRole, DeviceMethods.DataRequestRollback.Allowed));
        Assert.Equal((MethodKind.Command, Role.Owner, Transports.Local), (DeviceMethods.DataCancelRollback.Kind, DeviceMethods.DataCancelRollback.MinRole, DeviceMethods.DataCancelRollback.Allowed));
        Assert.Contains(DeviceMethods.DataRequestRollback, DeviceMethods.All);
        Assert.Equal("rolled-back", DataStates.RolledBack);
    }
}
