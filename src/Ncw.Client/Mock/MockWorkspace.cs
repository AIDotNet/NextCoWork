using System.Collections.Immutable;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Core.Layout;

namespace Ncw.Client.Mock;

/// <summary>
/// M1 的模拟数据:在协议(M2)接入之前,让三栏外壳、项目栏分组和状态角标有东西可看。
/// 只用于开发和截图测试,发布版不调用。
/// </summary>
public static class MockWorkspace
{
    public static ClientStores Create(DateTimeOffset now)
    {
        var stores = new ClientStores();
        Seed(stores, now);
        return stores;
    }

    public static void Seed(ClientStores stores, DateTimeOffset now)
    {
        var local = new DeviceInfo("local", "本机", DeviceKind.Local, DeviceConnection.Connected, null);
        var cloud = new DeviceInfo("cloud", "云端", DeviceKind.Cloud, DeviceConnection.Sleeping, null);
        var studio = new DeviceInfo("studio", "工作室 Mac mini", DeviceKind.Remote, DeviceConnection.Offline, null);

        var ncw = new ProjectRef("local", "nextcowork");
        var octopus = new ProjectRef("local", "octopus");
        var arc = new ProjectRef("local", "arc");
        var api = new ProjectRef("local", "api-server");
        var blog = new ProjectRef("cloud", "blog");
        var infra = new ProjectRef("studio", "infra");

        var projects = new[]
        {
            new ProjectInfo(ncw, "NextCoWork", "/Users/token/code/NextCoWork", EnvironmentKind.Local, null, "v3/avalonia", false),
            new ProjectInfo(octopus, "Octopus", "/Users/token/code/Octopus", EnvironmentKind.Local, null, "main", true),
            new ProjectInfo(arc, "TKCode.Arc", "/Users/token/code/TKCode.Arc", EnvironmentKind.Local, null, "main", true),
            new ProjectInfo(api, "api-server", "/srv/api", EnvironmentKind.Ssh, "dev-server", "release/2.4", true),
            new ProjectInfo(blog, "个人博客", "/workspace/blog", EnvironmentKind.Local, null, "main", true),
            new ProjectInfo(infra, "infra", "/Users/ops/infra", EnvironmentKind.Local, null, "main", true),
        };

        local = local with
        {
            Index = new ActiveRunIndex("local",
            [
                new ActiveRun("local", "run-1", "s-ncw-1", "nextcowork", 2),
                new ActiveRun("local", "run-2", "s-api-1", "api-server", 0),
            ]),
        };

        var sessions = ImmutableArray.Create(
            new SessionSummary("s-ncw-1", ncw, "设计 Agent Runtime 并实现骨架", now.AddMinutes(-3)),
            new SessionSummary("s-ncw-2", ncw, "Avalonia 三栏外壳", now.AddHours(-2)),
            new SessionSummary("s-ncw-3", ncw, "修复 CI 里的 headless 截图", now.AddDays(-1)),
            new SessionSummary("s-oct-1", octopus, "终端分屏重构", now.AddHours(-5)),
            new SessionSummary("s-api-1", api, "排查 release/2.4 的内存泄漏", now.AddMinutes(-12)),
            new SessionSummary("s-blog-1", blog, "写一篇 Avalonia 迁移的文章", now.AddDays(-3)));

        var terminals = ImmutableArray.Create(
            new TerminalSummary("t-ncw-1", ncw, "Claude Code", TerminalKind.ExternalAgent),
            new TerminalSummary("t-ncw-2", ncw, "zsh", TerminalKind.Shell),
            new TerminalSummary("t-api-1", api, "bash · dev-server", TerminalKind.Shell));

        stores.Workspace.Update(_ => new WorkspaceState(
            ImmutableDictionary.CreateRange(new[] { local, cloud, studio }.Select(d => KeyValuePair.Create(d.DeviceId, d))),
            projects.ToImmutableDictionary(p => p.Ref),
            sessions,
            terminals));

        var rail = RailLayoutOps.Reconcile(RailLayout.Empty, projects.Select(p => p.Ref));
        rail = RailLayoutOps.CreateGroup(rail, octopus, arc, new ProjectGroup("g-work", "工作", "#3B82F6", false));
        rail = RailLayoutOps.Move(rail, api, new RailTarget.InGroup("g-work", 2));
        rail = RailLayoutOps.CreateGroup(rail, blog, infra, new ProjectGroup("g-personal", "个人", "#8B5CF6", true));

        var appearance = ImmutableDictionary.CreateRange(new[]
        {
            KeyValuePair.Create(ncw, new ProjectAppearance("🐙", "#0F766E")),
            KeyValuePair.Create(octopus, new ProjectAppearance(null, "#F97316")),
            KeyValuePair.Create(arc, new ProjectAppearance("A", "#0D9488")),
            KeyValuePair.Create(api, new ProjectAppearance(null, "#2563EB")),
            KeyValuePair.Create(blog, new ProjectAppearance("博", "#DB2777")),
            KeyValuePair.Create(infra, new ProjectAppearance(null, "#4F46E5")),
        });

        stores.Layout.Update(_ => new LayoutState(rail, appearance));
        stores.Selection.Update(_ => new SelectionState(ncw, "s-ncw-1"));
    }
}
