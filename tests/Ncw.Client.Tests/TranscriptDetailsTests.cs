using Ncw.Client.ViewModels;

namespace Ncw.Client.Tests;

/// <summary>转录的小细节(docs/design/03 §11.5):项目相对路径、审查页签自动出现。</summary>
public class TranscriptDetailsTests
{
    [Theory]
    [InlineData("/Users/me/code/app/src/ui", "/Users/me/code/app", "src/ui")]
    [InlineData("/Users/me/code/app", "/Users/me/code/app/", "")]
    [InlineData("/Users/me/code/application/src", "/Users/me/code/app", "/Users/me/code/application/src")] // 前缀相同但不是子目录
    [InlineData("/srv/api/cmd/server", "/Users/me/code/app", "/srv/api/cmd/server")] // 项目外照原样
    [InlineData("/srv/api", null, "/srv/api")]
    public void Paths_inside_the_project_are_shown_relative(string dir, string? root, string expected) =>
        Assert.Equal(expected, ToolRowViewModel.RelativeDir(dir, root));

    [Fact]
    public void Review_tab_appears_when_the_session_changed_files_unless_the_user_added_it()
    {
        Assert.Equal([InspectorTabKind.Files], InspectorTabs.Compose([], hasSubagents: false));
        Assert.Equal([InspectorTabKind.Files, InspectorTabKind.Review, InspectorTabKind.Subagents], InspectorTabs.Compose([], hasSubagents: true, hasChanges: true));
        // 用户自己加过「审查」:按用户的位置,不重复
        Assert.Equal([InspectorTabKind.Files, InspectorTabKind.Git, InspectorTabKind.Review],
            InspectorTabs.Compose([InspectorTabKind.Git, InspectorTabKind.Review], hasSubagents: false, hasChanges: true));
    }
}

/// <summary>只有一台本机时,设置页不再到处写「设备 · 已连接 · 所有者」、不放只有一项的设备切换器(03 §11.7)。</summary>
public class SingleDeviceTests
{
    [Fact]
    public void Settings_scope_hides_the_device_dimension_for_a_lone_local_machine()
    {
        var stores = new Ncw.Client.Stores.ClientStores();
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem("local", new Ncw.Client.Stores.DeviceInfo("local", "本机", Ncw.Core.Domain.DeviceKind.Local, Ncw.Core.Domain.DeviceConnection.Connected, null, Role: "owner")),
        });
        var scope = new SettingsScope(stores);
        Assert.False(scope.ShowsDevices);
        Assert.False(scope.ShowsScopeDetails);
        Assert.False(scope.ShowsDeviceSwitcher);

        // 断开:状态要让人看到
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Connection = Ncw.Core.Domain.DeviceConnection.Offline }) });
        Assert.True(scope.ShowsScopeDetails);

        // 配对了第二台:设备维度回来
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem("studio", new Ncw.Client.Stores.DeviceInfo("studio", "工作室", Ncw.Core.Domain.DeviceKind.Remote, Ncw.Core.Domain.DeviceConnection.Connected, null, Role: "owner")),
        });
        Assert.True(scope.ShowsDevices);
        Assert.True(scope.ShowsDeviceSwitcher);
    }
}

public class RailInitialsTests
{
    [Theory]
    [InlineData("NextCoWork", "NC")]
    [InlineData("Avalonia 三栏外壳", "Av")]
    [InlineData("api-server", "AS")]
    [InlineData("agent", "Ag")]
    [InlineData("Avalonia", "Av")]
    [InlineData("x", "X")]
    [InlineData("项目一", "项")]
    [InlineData("  ", "?")]
    public void Default_icons_use_two_letters_so_projects_are_distinguishable(string name, string expected) =>
        Assert.Equal(expected, RailProjectViewModel.Initials(name));
}
