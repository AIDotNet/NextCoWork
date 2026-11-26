using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;

namespace Ncw.Client.Tests.Management;

/// <summary>执行环境管理页、SSH 交互、新建 SSH 项目、工作区 SSH 状态条与侧边栏离线状态的单元测试。</summary>
public class EnvironmentsPageTests
{
    [Fact]
    public async Task Loads_live_environments_respects_roles_and_marks_unknown_on_disconnect()
    {
        var rig = new ManagementRig("owner");
        var fake = new FakeEnvironments(rig.Stores);
        fake.Environments.Add(FakeEnvironments.SshDto("e1", "dev-server", EnvironmentStateNames.Ready, latencyMs: 14));
        fake.Push();
        var vm = new EnvironmentsViewModel(rig.Scope, rig.Stores, fake, () => new ScriptedDialogs());

        await vm.ActivateAsync();
        Assert.Equal(PageState.Ready, vm.State);
        Assert.True(vm.CanWrite);
        Assert.True(vm.CanConnect);
        Assert.True(vm.CanPickLocalFile);
        var row = Assert.Single(vm.Items);
        Assert.Equal(EnvironmentDisplayState.Ready, row.DisplayState);
        Assert.Equal(14, row.LatencyMs);

        // member 可查看和连接,但不能改配置
        rig.SetRole("member");
        Assert.True(vm.CanConnect);
        Assert.False(vm.CanWrite);
        Assert.False(vm.CanPickLocalFile);

        // 远程 owner 不能挑本机文件
        rig.SetRole("owner");
        rig.MakeRemote();
        Assert.True(vm.CanWrite);
        Assert.False(vm.CanPickLocalFile);

        // 设备断开:状态变未知(规则 7)
        rig.SetConnection(DeviceConnection.Offline);
        Assert.Equal(PageState.Unknown, vm.State);
        Assert.Equal(EnvironmentDisplayState.Unknown, Assert.Single(vm.Items).DisplayState);
    }

    [Fact]
    public async Task Editor_validates_fields_handles_target_change_confirm_and_clears_secrets()
    {
        var rig = new ManagementRig("owner");
        var fake = new FakeEnvironments(rig.Stores);
        fake.Push();
        var dialogs = new ScriptedDialogs { NextFile = ScriptedDialogs.File("id_ed25519", 512, "/Users/me/.ssh/id_ed25519") };
        var vm = new EnvironmentsViewModel(rig.Scope, rig.Stores, fake, () => dialogs);
        await vm.ActivateAsync();

        vm.NewCommand.Execute(null);
        Assert.True(vm.Editor.IsOpen);
        Assert.False(vm.Editor.CanSave);
        Assert.Contains(EnvironmentProblem.Name, vm.Editor.Problems);

        vm.Editor.Name = "prod-box";
        vm.Editor.Host = "10.0.0.8";
        vm.Editor.Port = "2222";
        vm.Editor.Username = "deploy";
        vm.Editor.Password = "super-secret";
        await vm.PickIdentityFileCommand.ExecuteAsync(null);
        Assert.Equal("/Users/me/.ssh/id_ed25519", vm.Editor.IdentityFile);
        Assert.Equal([FilePickKind.SshIdentityFile], dialogs.Picks);
        Assert.True(vm.Editor.CanSave);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.Editor.IsOpen);
        Assert.Equal("", vm.Editor.Password);
        var created = Assert.Single(fake.Created);
        Assert.Equal("super-secret", created.Secrets?.Password);
        Assert.Equal(2222, created.Profile.Target.Port);

        // 修改目标遇到 TargetChangeNeedsConfirm → 必须勾选确认后才能保存
        Assert.Single(vm.Items).EditCommand.Execute(null);
        Assert.True(vm.Editor.HasSavedPassword);
        vm.Editor.Host = "10.0.0.99";
        fake.NextUpdateFailure = R.Fail<EnvironmentDto>(ActionOutcome.Conflict, EnvironmentErrorKeys.TargetChangeNeedsConfirm);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.Editor.NeedsTargetChangeConfirm);
        Assert.False(vm.Editor.CanSave);

        vm.Editor.ConfirmTargetChange = true;
        Assert.True(vm.Editor.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.Editor.IsOpen);
        Assert.True(fake.Updated[^1].Confirm);
    }

    [Fact]
    public async Task Imports_ssh_config_manages_known_hosts_and_handles_connect_disconnect_remove()
    {
        var rig = new ManagementRig("owner");
        var fake = new FakeEnvironments(rig.Stores);
        fake.ImportCandidates.Add(new SshConfigCandidateDto("staging", "staging.internal", 22, "ops", true, "bastion", true, []));
        fake.ImportCandidates.Add(new SshConfigCandidateDto("broken", "x", 22, null, false, null, false, ["unsupported.proxyCommand"]));
        fake.KnownHosts.Add(new KnownHostDto("staging.internal", "ssh-ed25519", "ABCD1234"));
        fake.Push();

        var vm = new EnvironmentsViewModel(rig.Scope, rig.Stores, fake, () => new ScriptedDialogs());
        await vm.ActivateAsync();

        await vm.OpenImportCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.ImportCandidates.Count);
        Assert.True(vm.ImportCandidates[0].CanImport);
        Assert.False(vm.ImportCandidates[1].CanImport);

        await vm.ImportCandidates[0].ImportCommand.ExecuteAsync(null);
        Assert.True(vm.ImportCandidates[0].AlreadyImported);
        var row = Assert.Single(vm.Items);
        Assert.Equal("config", row.TargetKind);

        await row.ConnectCommand.ExecuteAsync(null);
        Assert.Equal(EnvironmentDisplayState.Ready, Assert.Single(vm.Items).DisplayState);
        await Assert.Single(vm.Items).DisconnectCommand.ExecuteAsync(null);
        Assert.Equal(EnvironmentDisplayState.Idle, Assert.Single(vm.Items).DisplayState);

        await vm.ToggleKnownHostsCommand.ExecuteAsync(null);
        var host = Assert.Single(vm.KnownHosts);
        await host.ForgetCommand.ExecuteAsync(null);
        Assert.Equal(["staging.internal"], fake.ForgottenHosts);
        Assert.Empty(vm.KnownHosts);

        Assert.Single(vm.Items).AskRemoveCommand.Execute(null);
        Assert.True(vm.HasPendingRemove);
        await vm.ConfirmRemoveCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task Ssh_interactions_handle_host_key_and_multi_prompt_credentials()
    {
        var rig = new ManagementRig("owner");
        var fake = new FakeEnvironments(rig.Stores);
        var vm = new SshInteractionsViewModel(rig.Stores, fake);
        var env = new EnvRefDto(ManagementRig.Device, "e1", "dev-server", "ssh");

        var hostKey = new SshHostKeyInteractionDto
        {
            Id = "i-hk",
            RunId = "env:e1:1",
            Version = 1,
            CreatedAt = 100,
            Env = env,
            HostName = "dev.internal",
            Algorithm = "ssh-ed25519",
            Fingerprint = "XYZ999",
        };
        var cred = new SshCredentialInteractionDto
        {
            Id = "i-cred",
            RunId = "env:e1:2",
            Version = 1,
            CreatedAt = 200,
            Env = env,
            PromptKind = "keyboardInteractive",
            HostName = "dev.internal",
            UserName = "ops",
            Instruction = "Enter OTP",
            Prompts = [new SshPromptDto("Password:", false), new SshPromptDto("OTP code:", true)],
            PreviousRejected = true,
            CanRemember = false,
            HasSaved = false,
        };

        rig.Stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, ManagementRig.Device, new InteractionsDto(1, [hostKey, cred])));
        Assert.Equal(2, vm.QueueCount);
        Assert.True(vm.ShowsGlobalOverlay);
        Assert.True(vm.Current!.IsHostKey);

        await vm.Current.TrustCommand.ExecuteAsync(null);
        Assert.Equal([("i-hk", true)], fake.HostKeyAnswers);

        // 只剩凭据交互
        rig.Stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, ManagementRig.Device, new InteractionsDto(2, [cred])));
        Assert.True(vm.Current!.IsCredential);
        Assert.True(vm.Current.PreviousRejected);
        Assert.Equal(2, vm.Current.Prompts.Count);
        Assert.False(vm.Current.SubmitCommand.CanExecute(null));

        vm.Current.Prompts[0].Value = "pw";
        vm.Current.Prompts[1].Value = "123456";
        Assert.True(vm.Current.SubmitCommand.CanExecute(null));
        await vm.Current.SubmitCommand.ExecuteAsync(null);

        var answered = Assert.Single(fake.CredentialAnswers);
        Assert.Equal("i-cred", answered.Id);
        Assert.Equal(["pw", "123456"], answered.Answers);
        Assert.False(answered.Remember);
        // 提交后清空内存中的明文
        Assert.All(vm.Current.Prompts, static p => Assert.Equal("", p.Value));

        vm.SuppressGlobalOverlay = true;
        Assert.False(vm.ShowsGlobalOverlay);
    }

    [Fact]
    public async Task New_project_dialog_browses_remote_directories_and_creates_ssh_project()
    {
        var rig = new ManagementRig("owner");
        var fake = new FakeEnvironments(rig.Stores);
        fake.Environments.Add(FakeEnvironments.SshDto("e1", "dev-server", EnvironmentStateNames.Ready));
        fake.Push();
        var actions = new ProjectActions(rig.Stores, new DeviceRegistry(), () => NoDialogs.Instance, () => DefaultClientText.Instance, fake);
        using var form = new NewProjectViewModel(rig.Stores, actions, () => NoDialogs.Instance, () => DefaultClientText.Instance, ManagementRig.Device, fake, initialSource: NewProjectSource.Ssh);

        await form.RefreshRemoteCommand.ExecuteAsync(null);
        Assert.True(form.IsSshSource);
        Assert.Equal("/home/ops", form.Path);
        Assert.Single(form.Entries); // 默认隐藏 .git
        Assert.Equal("api", form.Entries[0].Name);

        form.ShowHidden = true;
        Assert.Equal(2, form.Entries.Count);

        await form.NavigateToCommand.ExecuteAsync("/home/ops/api");
        Assert.Equal("/home/ops/api", form.Path);

        form.Name = "Backend API";
        var ok = await form.SubmitAsync();
        Assert.True(ok);
        Assert.Equal(new ProjectRef(ManagementRig.Device, "ssh-1"), form.Created);
        Assert.Equal([("e1", "/home/ops/api", "Backend API")], fake.AddedProjects);
    }

    [Fact]
    public async Task Status_bar_and_sidebar_reflect_ssh_environment_failure_and_reconnect()
    {
        var rig = new ManagementRig("owner");
        var sshProject = new ProjectRef(ManagementRig.Device, "ws-ssh");
        rig.Stores.Workspace.Update(w => w with
        {
            Projects = w.Projects.Add(sshProject, new ProjectInfo(sshProject, "api-server", "/srv/api", EnvironmentKind.Ssh, "dev-server", "main", true, "e1")),
        });
        rig.Stores.Layout.Update(l => l with { Rail = new Ncw.Core.Layout.RailLayout([new Ncw.Core.Layout.RailNode.Project(sshProject)]) });
        rig.Stores.Selection.Update(_ => new SelectionState(sshProject, null));

        var fake = new FakeEnvironments(rig.Stores);
        var mismatch = new HostKeyMismatchDto("10.0.0.5", "ssh-ed25519", "NEWKEY", [new PinnedKeyDto("ssh-ed25519", "OLDKEY")]);
        fake.Environments.Add(FakeEnvironments.SshDto("e1", "dev-server", EnvironmentStateNames.Failed, error: new EnvironmentErrorDto(EnvironmentErrorKeys.HostKeyChanged, mismatch)));
        fake.Push();

        var opened = false;
        var bar = new SshStatusBarViewModel(rig.Stores, fake, () => opened = true);
        var rail = new RailViewModel(rig.Stores, new LayoutActions(rig.Stores, () => DefaultClientText.Instance));

        Assert.True(bar.IsVisible);
        Assert.True(bar.IsFailed);
        Assert.True(bar.HasHostKeyMismatch);
        Assert.Equal(EnvironmentErrorKeys.HostKeyChanged, bar.ErrorKey);

        var tile = Assert.IsType<RailProjectViewModel>(Assert.Single(rail.Items));
        Assert.True(tile.IsEnvironmentDisconnected);
        Assert.True(tile.IsOffline);

        bar.OpenSettingsCommand.Execute(null);
        Assert.True(opened);

        await bar.ReconnectCommand.ExecuteAsync(null);
        Assert.Equal(["e1"], fake.Connected);
        Assert.False(bar.IsVisible);
        Assert.False(tile.IsEnvironmentDisconnected);
        Assert.False(tile.IsOffline);
    }
}
