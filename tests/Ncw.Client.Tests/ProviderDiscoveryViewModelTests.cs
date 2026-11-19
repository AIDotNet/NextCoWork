using Ncw.Client.Actions;
using Ncw.Client.ViewModels;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests;

/// <summary>
/// 模型页的「从其他应用导入」(docs/design/07 §3「供应商自动发现与导入」):自动扫一次、不支持就不发请求、默认勾选、
/// 导入只传候选 id、导入后以设备重扫的状态为准、选中第一个新供应商、换设备清掉旧结果。
/// </summary>
public class ProviderDiscoveryViewModelTests
{
    internal sealed class FakeCommands : IProviderDiscoveryCommands
    {
        public bool Supported { get; set; } = true;

        public List<DiscoveredProviderDto> Candidates { get; set; } = [];

        public List<string> Scans { get; } = [];

        public List<IReadOnlyList<string>> Imports { get; } = [];

        public Func<IReadOnlyList<string>, DiscoveryImportResult> OnImport { get; set; } = ids => new([.. ids.Select(id => new DiscoveryImportItemDto(id, "p-" + id, null))]);

        public TaskCompletionSource? Gate { get; set; }

        public bool SupportsDiscovery(string deviceId) => Supported;

        public async Task<ActionResult<DiscoveryScanResult>> ScanDiscoveryAsync(string deviceId, CancellationToken ct = default)
        {
            Scans.Add(deviceId);
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return new ActionResult<DiscoveryScanResult>(ActionOutcome.Ok, new DiscoveryScanResult([.. Candidates]));
        }

        public Task<ActionResult<DiscoveryImportResult>> ImportDiscoveredAsync(string deviceId, IReadOnlyList<string> candidateIds, CancellationToken ct = default)
        {
            Imports.Add(candidateIds);
            var result = OnImport(candidateIds);
            // 设备上导入成功的那几条,下次扫描就是「已导入」
            Candidates = [.. Candidates.Select(c => result.Items.Any(i => i.CandidateId == c.Id && i.ProviderId is not null) ? c with { Status = DiscoveryStatus.Imported } : c)];
            return Task.FromResult(new ActionResult<DiscoveryImportResult>(ActionOutcome.Ok, result));
        }
    }

    internal static DiscoveredProviderDto Candidate(string id, string status = DiscoveryStatus.New, string source = DiscoverySources.ClaudeCode) =>
        new(id, source, "~/.claude/settings.json", [], id, id.ToUpperInvariant(), "anthropic", $"https://{id}.example.com", "1234", ["m-" + id], status, null);

    [Fact]
    public void Activating_scans_once_per_device_and_checks_new_candidates_by_default()
    {
        var commands = new FakeCommands { Candidates = [Candidate("a"), Candidate("b", DiscoveryStatus.Conflict), Candidate("c", DiscoveryStatus.Imported)] };
        var vm = new ProviderDiscoveryViewModel(commands, _ => { });

        vm.Activate("dev", canEdit: true);
        vm.Activate("dev", canEdit: true); // 同一台设备不重扫

        Assert.Equal(["dev"], commands.Scans);
        Assert.Equal(DiscoveryState.Ready, vm.State);
        Assert.Equal([true, false, false], vm.Items.Select(i => i.IsChecked));   // 新的勾上;冲突的让用户自己决定;已导入的不能选
        Assert.Equal([true, true, false], vm.Items.Select(i => i.CanSelect));
        Assert.Equal(2, vm.ImportableCount);
        Assert.True(vm.CanImport);

        vm.Items[0].IsChecked = false;
        Assert.False(vm.CanImport);
        Assert.False(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public void Old_devices_and_non_owners_are_not_asked()
    {
        var commands = new FakeCommands { Supported = false };
        var vm = new ProviderDiscoveryViewModel(commands, _ => { });
        vm.Activate("dev", canEdit: true);
        Assert.Equal(DiscoveryState.Unsupported, vm.State);

        commands.Supported = true;
        var member = new ProviderDiscoveryViewModel(commands, _ => { });
        member.Activate("dev", canEdit: false);
        Assert.Equal(DiscoveryState.Unsupported, member.State);
        Assert.Empty(commands.Scans);
    }

    [Fact]
    public async Task Import_sends_only_candidate_ids_rescans_and_selects_the_first_new_provider()
    {
        var commands = new FakeCommands
        {
            Candidates = [Candidate("a"), Candidate("b"), Candidate("c", DiscoveryStatus.Conflict)],
            OnImport = ids => new([new DiscoveryImportItemDto("a", "a", null), new DiscoveryImportItemDto("b", null, DiscoveryErrorKeys.Gone)]),
        };
        string? selected = null;
        var vm = new ProviderDiscoveryViewModel(commands, id => selected = id);
        vm.Activate("dev", canEdit: true);

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Equal(["a", "b"], Assert.Single(commands.Imports)); // 只传勾上的候选 id
        Assert.Equal(2, commands.Scans.Count);                     // 导入后重扫,状态以设备为准
        Assert.Equal("a", selected);
        Assert.Equal(1, vm.LastImportedCount);
        Assert.True(vm.Items.Single(i => i.Id == "a").IsImported);
        Assert.Equal(DiscoveryErrorKeys.Gone, vm.Items.Single(i => i.Id == "b").ErrorKey);
        Assert.Equal(2, vm.ImportableCount);
    }

    [Fact]
    public async Task Switching_devices_drops_the_previous_results_and_a_late_answer_is_ignored()
    {
        var first = new TaskCompletionSource();
        var commands = new FakeCommands { Candidates = [Candidate("a")], Gate = first };
        var vm = new ProviderDiscoveryViewModel(commands, _ => { });
        vm.Activate("dev-1", canEdit: true);
        Assert.True(vm.IsScanning);

        vm.Reset();
        commands.Gate = null;
        commands.Candidates = [Candidate("x"), Candidate("y")];
        vm.Activate("dev-2", canEdit: true);
        Assert.Equal(["x", "y"], vm.Items.Select(i => i.Id));

        // dev-1 那次扫描这时才回来:不能覆盖 dev-2 的结果
        commands.Candidates = [Candidate("a")];
        first.SetResult();
        await Task.Yield();
        Assert.Equal(["x", "y"], vm.Items.Select(i => i.Id));
        Assert.Equal(DiscoveryState.Ready, vm.State);
    }

    [Fact]
    public void The_models_page_has_a_discovery_pane_next_to_the_catalog_and_the_providers()
    {
        var models = new ShellViewModel(Ncw.Client.Mock.MockWorkspace.Create(DateTimeOffset.Now)).Settings.Models;
        Assert.True(models.ShowsCatalog);

        models.SelectDiscoveryCommand.Execute(null);
        Assert.True(models.ShowsDiscovery);
        Assert.False(models.ShowsCatalog);

        models.SelectCatalogCommand.Execute(null);
        Assert.False(models.ShowsDiscovery);
        Assert.True(models.ShowsCatalog);
    }
}
