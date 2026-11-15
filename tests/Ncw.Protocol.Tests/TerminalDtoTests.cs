using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 终端的线上形状:每个类型往返、方法表声明齐全(种类 / 角色 / 能力)、主题名。
/// 冻结的向量在 <see cref="GoldenV1Terminals"/>(只增向量,不改已冻结的旧向量)。
/// </summary>
public class TerminalDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    private static readonly TerminalEnvDto Local = new("dev1", "local", "Local", "local");

    private static TerminalDto Running() => new("t1", "p1", Local, "ws", "/p1", "/bin/zsh", 80, 24, "running", 1_700_000_000_000);

    private static TerminalDto Exited() =>
        new("t1", "p1", new TerminalEnvDto("dev1", "dev-server", "dev-server", "ssh"), "ws", "/srv/ws", "/bin/bash", 120, 40, "exited", 1_700_000_000_000, 130, 2, "killed", 1_700_000_005_000);

    private static TerminalIntentDto Intent() => new("i1", "t1", "p1", new TerminalEnvDto("dev1", "dev-server", "dev-server", "ssh"), "/srv/ws", "/bin/bash", 1_700_000_060_000);

    [Fact]
    public void Every_terminal_type_round_trips()
    {
        Assert.Equal(Hex(Running()), Hex(RoundTrip(Running())));
        var exited = RoundTrip(Exited());
        Assert.Equal(("exited", 130, 2, "killed", "ssh"), (exited.Status, exited.ExitCode, exited.Signal, exited.ExitReason, exited.Environment.Kind));

        var ready = RoundTrip(new TerminalPrepareResult(Terminal: Running()));
        Assert.NotNull(ready.Terminal);
        Assert.Null(ready.Intent);
        var approval = RoundTrip(new TerminalPrepareResult(Intent: Intent()));
        Assert.Null(approval.Terminal);
        Assert.Equal(("dev1", "dev-server"), (approval.Intent!.Environment.DeviceId, approval.Intent.Environment.Environment));

        byte[] bytes = [0, 1, 0xE4, 0xBD, 0xA0, 0xFF];
        Assert.Equal(bytes, RoundTrip(new TerminalsWriteParams("t1", bytes)).Data);
        Assert.Equal(bytes, RoundTrip(new TerminalDataDto(bytes)).Data);
        var snapshot = RoundTrip(new TerminalSnapshotDto(Running(), bytes, true, 42));
        Assert.Equal(bytes, snapshot.Data);
        Assert.Equal((true, 42L), (snapshot.Truncated, snapshot.Seq));
        Assert.Equal((null, null, "exited"), (RoundTrip(new TerminalExitDto(null, null, "exited")).ExitCode, RoundTrip(new TerminalExitDto(null, null, "exited")).Signal, "exited"));
        Assert.Equal(("terminals.notFound", null), (RoundTrip(new TerminalErrorDto("terminals.notFound")).Key, RoundTrip(new TerminalErrorDto("terminals.notFound")).Detail));
        Assert.Equal("tok", RoundTrip(new TerminalApproveResult("tok")).Approval);
        Assert.Equal(("p1", "t1", "tok"), (RoundTrip(new TerminalsCreateParams("p1", 80, 24, "t1", null, "tok")).ProjectId, RoundTrip(new TerminalsCreateParams("p1", 80, 24, "t1", null, "tok")).Id, RoundTrip(new TerminalsCreateParams("p1", 80, 24, "t1", null, "tok")).Approval));
        Assert.Equal(new TerminalBufferDto("t1", bytes, false, 3).Seq, RoundTrip(new TerminalBufferDto("t1", bytes, false, 3)).Seq);
    }

    [Fact]
    public void The_method_table_declares_kind_role_and_capability_for_every_method()
    {
        Assert.Equal(9, TerminalMethods.All.Count);
        Assert.All(TerminalMethods.All, m =>
        {
            Assert.Equal(Capabilities.Terminals, m.Capability);
            Assert.Equal(Transports.Any, m.Allowed);
            Assert.StartsWith("terminals.", m.Name);
        });

        // 读:viewer;改:member,且全部是 command(必带幂等键)
        MethodDef[] reads = [TerminalMethods.List, TerminalMethods.GetBuffer];
        Assert.All(reads, m => Assert.Equal((MethodKind.Query, Role.Viewer), (m.Kind, m.MinRole)));
        var writes = TerminalMethods.All.Except(reads).ToList();
        Assert.Equal(7, writes.Count);
        Assert.All(writes, m => Assert.Equal((MethodKind.Command, Role.Member), (m.Kind, m.MinRole)));
        Assert.Equal(TerminalMethods.All.Count, TerminalMethods.All.Select(m => m.Name).Distinct().Count());
    }

    [Fact]
    public void The_topic_name_is_terminal_slash_id()
    {
        Assert.Equal("terminal/t1", TerminalMethods.Topic("t1").Name);
        Assert.Equal("terminal/t1", TerminalMethods.TopicName("t1"));
        Assert.StartsWith(TerminalMethods.TopicPrefix, TerminalMethods.Topic("x").Name);
    }

    [Fact]
    public void Golden_vectors_v1_terminals()
    {
        Assert.Equal(GoldenV1Terminals.Terminal, Hex(Running()));
        Assert.Equal(GoldenV1Terminals.ExitedTerminal, Hex(Exited()));
        Assert.Equal(GoldenV1Terminals.PrepareReady, Hex(new TerminalPrepareResult(Terminal: Running())));
        Assert.Equal(GoldenV1Terminals.PrepareApproval, Hex(new TerminalPrepareResult(Intent: Intent())));
        Assert.Equal(GoldenV1Terminals.Snapshot, Hex(new TerminalSnapshotDto(Running(), [0x68, 0x69, 0x0d, 0x0a], false, 7)));
        Assert.Equal(GoldenV1Terminals.Data, Hex(new TerminalDataDto([0x68, 0x69])));
        Assert.Equal(GoldenV1Terminals.Exit, Hex(new TerminalExitDto(0, null, "exited")));
        Assert.Equal(GoldenV1Terminals.WriteParams, Hex(new TerminalsWriteParams("t1", [0x65, 0x63, 0x68, 0x6f, 0x0a])));
    }
}
