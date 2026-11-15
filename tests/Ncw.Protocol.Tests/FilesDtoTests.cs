using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// 文件方法的线上形状:每个类型往返、方法表声明齐全(种类、角色、能力、承载)、错误 key 稳定。冻结的向量在 <see cref="GoldenV1Files"/>
/// (只增向量,不改已冻结的旧向量):这些测试失败说明某个 DTO 的键名 / 形状变了,那必须升协议版本或加能力开关,不能直接改向量。
/// </summary>
public class FilesDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    private static readonly FilesListResult Listing = new(
        "src",
        [new FileEntryDto("app.ts", "src/app.ts", "file", false, false, false, 120, 1_700_000_000_000), new FileEntryDto("lib", "src/lib", "dir", true, false, true)],
        2, false);

    private static readonly FileContentDto Text = new("text", "a.txt", 5, "ab12", Content: "hello");
    private static readonly FileContentDto Image = new("image", "p.png", 3, "cd34", Mime: "image/png", Data: [1, 2, 3]);
    private static readonly FileContentDto Binary = new("binary", "d.pdf", 9, "ef56", Reason: "unsupported");
    private static readonly FilesWriteParams WriteText = new("w1", "a.txt", "ab12", Content: "new");
    private static readonly FilesMutateParams Mutate = new("w1", "a.txt", FileOperations.Rename, "b.txt", true);
    private static readonly FilesSearchResult Search = new([new FileMatchDto("a.cs", 3, 7, "var x = 1;")], 12, true);
    private static readonly FilesResourceDto Resource = new(
        ResourceKinds.Agent, ResourceScopes.Project, "reviewer",
        [new FrontmatterEntryDto("name", "reviewer"), new FrontmatterEntryDto("tools", Items: ["Read", "Grep"])], "你是一个审查者", ["nested.a"], "638000000000000000:42");

    [Fact]
    public void Every_files_payload_round_trips_with_optional_fields_intact()
    {
        Assert.Equal(Hex(Listing), Hex(RoundTrip(Listing)));
        Assert.Equal(Hex(Text), Hex(RoundTrip(Text)));
        Assert.Equal(Hex(Image), Hex(RoundTrip(Image)));
        Assert.Equal(Hex(Binary), Hex(RoundTrip(Binary)));
        Assert.Equal(Hex(WriteText), Hex(RoundTrip(WriteText)));
        Assert.Equal(Hex(Mutate), Hex(RoundTrip(Mutate)));
        Assert.Equal(Hex(Search), Hex(RoundTrip(Search)));
        Assert.Equal(Hex(Resource), Hex(RoundTrip(Resource)));

        var back = RoundTrip(Resource);
        Assert.Equal(["Read", "Grep"], back.Frontmatter[1].Items!);
        Assert.Null(back.Frontmatter[1].Value);
        Assert.Equal([1, 2, 3], RoundTrip(Image).Data!);
        Assert.Null(RoundTrip(Text).Data);
    }

    [Fact]
    public void Golden_vectors_v1_files()
    {
        Assert.Equal(GoldenV1Files.ListResult, Hex(Listing));
        Assert.Equal(GoldenV1Files.TextContent, Hex(Text));
        Assert.Equal(GoldenV1Files.ImageContent, Hex(Image));
        Assert.Equal(GoldenV1Files.BinaryContent, Hex(Binary));
        Assert.Equal(GoldenV1Files.WriteParams, Hex(WriteText));
        Assert.Equal(GoldenV1Files.MutateParams, Hex(Mutate));
        Assert.Equal(GoldenV1Files.SearchResult, Hex(Search));
        Assert.Equal(GoldenV1Files.ResourceFile, Hex(Resource));
    }

    [Fact]
    public void The_method_table_declares_kind_role_capability_and_transport_for_every_method()
    {
        Assert.Equal(10, FilesMethods.All.Count);
        Assert.Equal(FilesMethods.All.Count, FilesMethods.All.Select(m => m.Name).Distinct().Count());
        Assert.All(FilesMethods.All, m =>
        {
            Assert.StartsWith("files.", m.Name, StringComparison.Ordinal);
            Assert.Equal(Capabilities.Files, m.Capability);
            Assert.Equal(Transports.Any, m.Allowed);
        });

        // 读 viewer、写 member;command 一律是写
        foreach (var m in new MethodDef[] { FilesMethods.List, FilesMethods.Read, FilesMethods.ReadRange, FilesMethods.Suggest, FilesMethods.Search, FilesMethods.ResourceGet })
        {
            Assert.Equal((MethodKind.Query, Role.Viewer), (m.Kind, m.MinRole));
        }

        foreach (var m in new MethodDef[] { FilesMethods.Write, FilesMethods.Mutate, FilesMethods.ResourceSave, FilesMethods.ResourceDelete })
        {
            Assert.Equal((MethodKind.Command, Role.Member), (m.Kind, m.MinRole));
        }
    }

    [Fact]
    public void Limits_are_consistent_with_the_frame_size()
    {
        // 内联图片 + 开销必须放得进一帧;分块上限小于内联上限
        Assert.True(FilesLimits.InlineImageMax + 4096 < ProtocolInfo.MaxFrameBytes);
        Assert.True(FilesLimits.RangeMax < FilesLimits.InlineImageMax);
        Assert.True(FilesLimits.TextMax + 4096 < ProtocolInfo.MaxFrameBytes);
    }

    [Fact]
    public void Error_keys_are_stable_strings()
    {
        Assert.Equal("files.conflict", FilesErrorKeys.Conflict);
        Assert.Equal("files.invalidPath", FilesErrorKeys.InvalidPath);
        Assert.Equal("files.exists", FilesErrorKeys.Exists);
        Assert.Equal("files.symlink", FilesErrorKeys.Symlink);
        Assert.Equal("files.invalidPattern", FilesErrorKeys.InvalidPattern);
        var back = RoundTrip(new FilesErrorDto(FilesErrorKeys.Conflict, "stale revision"));
        Assert.Equal(("files.conflict", "stale revision"), (back.Key, back.Detail));
    }
}
