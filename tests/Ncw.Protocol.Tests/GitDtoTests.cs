using Ncw.Protocol.Methods;
using Ncw.Protocol.Wire;

namespace Ncw.Protocol.Tests;

/// <summary>
/// Git 方法的线上形状:每个类型往返、方法表声明齐全(种类 / 角色 / 能力)、错误 details 的形状。
/// 冻结的向量在 <see cref="GoldenV1Git"/>(只增向量,不改已冻结的旧向量)。
/// </summary>
public class GitDtoTests
{
    private static T RoundTrip<T>(T value) where T : class
    {
        var shape = PayloadShapes.GetShape<T>();
        return FrameCodec.DecodePayload(FrameCodec.EncodePayload(value, shape), shape);
    }

    private static string Hex<T>(T value) => Convert.ToHexStringLower(FrameCodec.EncodePayload(value, PayloadShapes.GetShape<T>()));

    private static readonly GitOverviewDto Overview = new(
        true, null, "/repo", "main", false, false, "origin/main", 2, 3,
        [
            new GitFileChangeDto("src/a.cs", "M", ".", true, false, false, false),
            new GitFileChangeDto("src/新 文件.cs", "R", "M", true, true, false, false, "src/old.cs"),
            new GitFileChangeDto("src/u.cs", "U", "U", false, false, false, true),
            new GitFileChangeDto("fresh.txt", "?", "?", false, false, true, false),
        ],
        false);

    [Fact]
    public void Overview_round_trips_including_files_and_the_unavailable_form()
    {
        var back = RoundTrip(Overview);

        Assert.Equal(Overview.Files!.Length, back.Files!.Length);
        Assert.Equal("src/old.cs", back.Files[1].RenamedFrom);
        Assert.True(back.Files[2].Conflicted);
        Assert.Equal(Hex(Overview), Hex(back));

        var unavailable = RoundTrip(new GitOverviewDto(false, GitUnavailableReasons.NotRepository));
        Assert.False(unavailable.Available);
        Assert.Equal("notRepository", unavailable.Reason);
        Assert.Null(unavailable.Files);
    }

    [Fact]
    public void Every_other_git_type_round_trips()
    {
        Assert.Equal(("src/a.cs", true, false, "diff"), (RoundTrip(new GitDiffDto("src/a.cs", true, false, false, "diff")) is var d ? (d.Path, d.Staged, d.Truncated, d.Text) : default));
        Assert.Equal(new GitDiffSidesDto(true, "a\n", "b\n"), RoundTrip(new GitDiffSidesDto(true, "a\n", "b\n")));
        Assert.False(RoundTrip(new GitDiffSidesDto(false)).Comparable);
        Assert.Equal(new GitCommitDto("abc123", "abc", "feat: 中文", "张三", 1_700_000_000_000), RoundTrip(new GitCommitDto("abc123", "abc", "feat: 中文", "张三", 1_700_000_000_000)));
        Assert.Equal(["a b", "中文.md"], RoundTrip(new GitPathsParams("w1", ["a b", "中文.md"])).Paths);
        Assert.Equal(new GitCreateBranchParams("w1", "feature/x", true), RoundTrip(new GitCreateBranchParams("w1", "feature/x", true)));
        Assert.Equal(new GitBranchDto("main", true, "origin/main"), RoundTrip(new GitBranchDto("main", true, "origin/main")));
        Assert.Equal(2, RoundTrip(new GitCommitsResult([new GitCommitDto("a", "a", "s", "n", 1), new GitCommitDto("b", "b", "s", "n", 2)])).Commits.Length);
        Assert.Equal(2, RoundTrip(new GitBranchesResult([new GitBranchDto("a", true, ""), new GitBranchDto("b", false, "")])).Branches.Length);
        Assert.Equal("feat: x", RoundTrip(new GitGeneratedMessageDto("feat: x")).Message);
        Assert.Equal(new GitErrorDto("git.failed", "fatal: bad"), RoundTrip(new GitErrorDto("git.failed", "fatal: bad")));
        Assert.Equal(new GitCommitsParams("w1", 20), RoundTrip(new GitCommitsParams("w1", 20)));
    }

    [Fact]
    public void The_method_table_declares_kind_role_and_capability_for_every_method()
    {
        Assert.Equal(13, GitMethods.All.Count);
        Assert.Equal(GitMethods.All.Count, GitMethods.All.Select(m => m.Name).Distinct().Count());
        foreach (var method in GitMethods.All)
        {
            Assert.StartsWith("git.", method.Name);
            Assert.Equal(Capabilities.Git, method.Capability);
            Assert.Equal(Transports.Any, method.Allowed);
            // 读取 viewer 起;写入至少 member(路由据此要求幂等键)
            if (method.Kind == MethodKind.Command)
            {
                Assert.Equal(Role.Member, method.MinRole);
            }
        }

        Assert.All(new MethodDef[] { GitMethods.Overview, GitMethods.Branches, GitMethods.Commits, GitMethods.Diff, GitMethods.DiffSides },
            m => Assert.Equal((MethodKind.Query, Role.Viewer), (m.Kind, m.MinRole)));
        // 生成提交信息没有副作用但会花钱:member 起的 query
        Assert.Equal((MethodKind.Query, Role.Member), (GitMethods.GenerateMessage.Kind, GitMethods.GenerateMessage.MinRole));
        Assert.All(new MethodDef[] { GitMethods.Stage, GitMethods.Unstage, GitMethods.Commit, GitMethods.Checkout, GitMethods.CreateBranch, GitMethods.Pull, GitMethods.Push },
            m => Assert.Equal(MethodKind.Command, m.Kind));
    }

    [Fact]
    public void Golden_vectors_v1_git()
    {
        Assert.Equal(GoldenV1Git.Overview, Hex(Overview));
        Assert.Equal(GoldenV1Git.OverviewUnavailable, Hex(new GitOverviewDto(false, GitUnavailableReasons.GitMissing)));
        Assert.Equal(GoldenV1Git.Diff, Hex(new GitDiffDto("src/a.cs", false, false, true, "@@ -1 +1 @@\n-a\n+b\n")));
        Assert.Equal(GoldenV1Git.DiffSides, Hex(new GitDiffSidesDto(true, "one\n", "two\n")));
        Assert.Equal(GoldenV1Git.Commit, Hex(new GitCommitDto("0123456789abcdef0123456789abcdef01234567", "0123456", "feat: add", "Test", 1_700_000_000_000)));
        Assert.Equal(GoldenV1Git.PathsParams, Hex(new GitPathsParams("w1", ["a.txt", "中文/b c.md"])));
        Assert.Equal(GoldenV1Git.CreateBranchParams, Hex(new GitCreateBranchParams("w1", "feature/x", true)));
        Assert.Equal(GoldenV1Git.ErrorDetails, Hex(new GitErrorDto("git.failed", "fatal: not a git repository")));
    }
}
