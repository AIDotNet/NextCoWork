using Ncw.Agent.Parity.Tests.Compare;
using Ncw.Agent.Parity.Tests.Rig;

namespace Ncw.Agent.Parity.Tests;

/// <summary>规则本身的纪律:每条预期差异都引用一条真实存在于文档里的登记;没有哪条规则是摆设。</summary>
public sealed class RuleRegistryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "NextCoWork.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Cannot find the repository root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_rule_quotes_a_registered_entry_that_really_is_in_the_docs()
    {
        var root = RepoRoot();
        var missing = Rules.All
            .Where(r => !File.ReadAllText(Path.Combine(root, r.Doc.File)).Contains(r.Doc.Quote, StringComparison.Ordinal))
            .Select(r => $"{r.Id}: \"{r.Doc.Quote}\" is not in {r.Doc.File}")
            .ToList();
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [Fact]
    public void Rule_ids_are_unique_and_every_rule_explains_itself()
    {
        Assert.Equal(Rules.All.Count, Rules.All.Select(r => r.Id).Distinct().Count());
        Assert.All(Rules.All, r => Assert.True(r.Why.Length > 20, $"{r.Id} needs a reason"));
    }

    [Fact]
    public async Task Every_rule_is_exercised_by_at_least_one_recording()
    {
        // Windows 上跑不了依赖 POSIX shell 的录制(Recording.NeedsPosixShell),只被它们用到的规则会被误判成「没人用」
        Assert.SkipWhen(OperatingSystem.IsWindows(), "some recordings need a POSIX shell; the full rule ledger is checked on macOS / Linux");
        var used = new HashSet<string>();
        foreach (var recording in Recording.LoadAll())
        {
            var replayed = await Replay.RunAsync(recording);
            var (_, ledger) = Comparer.Compare(recording, replayed);
            used.UnionWith(ledger.Applied.Keys);
        }

        var dead = Rules.All.Where(r => !used.Contains(r.Id)).Select(r => r.Id).ToList();
        Assert.True(dead.Count == 0, "Rules that no recording needs (remove them): " + string.Join(", ", dead));
    }
}
