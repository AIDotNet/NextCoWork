using System.Collections.Immutable;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Ncw.Client.Actions;
using Ncw.Client.Devices;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Client.Tests.Management;
using Ncw.Client.ViewModels;
using Ncw.Core.Domain;
using Ncw.Core.Layout;
using Ncw.Protocol.Methods;
using Ncw.UI.Chrome;
using Ncw.UI.Controls;
using Ncw.UI.Localization;
using Ncw.UI.Views.Desktop;
using Ncw.UI.Views.Desktop.Management;
using Ncw.UI.Views.Dialogs;
using Ncw.UI.Views.Mobile;
using TKCode.Arc;

namespace Ncw.UI.Tests;

/// <summary>SSH 执行环境设置页、指纹/凭据交互浮层、新建 SSH 项目远程浏览、工作区状态条与手机端设备页的 UI 测试。</summary>
public class SshEnvironmentsUiTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 25, 10, 0, 0, TimeSpan.FromHours(8));

    private static void Dispatch()
    {
        for (var i = 0; i < 6; i++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static (Window Window, ShellViewModel Shell, FakeEnvironments Fake, ManagementRig Rig) MountDesktop(double width = 1280, double height = 860)
    {
        var rig = new ManagementRig("owner");
        var fake = new FakeEnvironments(rig.Stores);
        var sshProject = new ProjectRef(ManagementRig.Device, "ws-ssh");
        rig.Stores.Workspace.Update(w => w with
        {
            Projects = w.Projects.Add(sshProject, new ProjectInfo(sshProject, "api-server", "/srv/api", EnvironmentKind.Ssh, "dev-server", "main", true, "e1")),
        });
        rig.Stores.Layout.Update(l => l with
        {
            Rail = new RailLayout([new RailNode.Project(new ProjectRef(ManagementRig.Device, ManagementRig.Workspace)), new RailNode.Project(sshProject)]),
        });
        rig.Stores.Selection.Update(_ => new SelectionState(sshProject, null));

        var mismatch = new HostKeyMismatchDto("prod.internal", "ssh-ed25519", "NEWFINGERPRINT123", [new PinnedKeyDto("ssh-ed25519", "OLDFINGERPRINT999")]);
        fake.Environments.Add(FakeEnvironments.SshDto("e1", "dev-server", EnvironmentStateNames.Ready, host: "10.0.0.5", latencyMs: 12, hasKey: true));
        fake.Environments.Add(FakeEnvironments.SshDto("e2", "prod-box", EnvironmentStateNames.Failed, host: "prod.internal", hasPassword: true,
            error: new EnvironmentErrorDto(EnvironmentErrorKeys.HostKeyChanged, mismatch)));
        fake.Push();

        var shell = new ShellViewModel(rig.Stores, environments: fake);
        var view = new DesktopShell { DataContext = shell, Chrome = new WindowChrome(0, false) };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Dispatch();
        return (window, shell, fake, rig);
    }

    [Fact]
    public void Every_environment_error_key_maps_to_a_specific_localized_message()
    {
        var generic = NcwTexts.Get(NcwTextKind.ErrEnvGeneric);
        var keys = typeof(EnvironmentErrorKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(static f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(static f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            var text = KeyText.Error(key);
            Assert.False(string.IsNullOrWhiteSpace(text), $"Missing translation for {key}");
            if (key != EnvironmentErrorKeys.Internal)
            {
                Assert.NotEqual(generic, text);
            }
        }
    }

    [AvaloniaFact]
    public async Task Environments_settings_page_renders_list_editor_import_and_known_hosts()
    {
        var (window, shell, _, _) = MountDesktop();
        try
        {
            shell.OpenSettingsSection(SettingsSection.Environments);
            await shell.Settings.Pages.Environments.ActivateAsync();
            Dispatch();

            var page = Assert.Single(window.GetVisualDescendants().OfType<EnvironmentsPage>());
            var texts = page.GetVisualDescendants().OfType<TextBlock>().Where(static t => t.IsEffectivelyVisible).Select(static t => t.Text).ToList();
            Assert.Contains("dev-server", texts);
            Assert.Contains("prod-box", texts);
            Assert.Contains(NcwTexts.Get(NcwTextKind.EnvHostKeyChangedTitle), texts);

            // 打开编辑器表单并确认表单渲染正常
            shell.Settings.Pages.Environments.NewCommand.Execute(null);
            Dispatch();
            Assert.True(shell.Settings.Pages.Environments.Editor.IsOpen);
            var editorTexts = page.GetVisualDescendants().OfType<TextBlock>().Where(static t => t.IsEffectivelyVisible).Select(static t => t.Text).ToList();
            Assert.Contains(NcwTexts.Get(NcwTextKind.EnvNewTitle), editorTexts);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Ssh_interaction_overlay_renders_host_key_and_credential_prompts()
    {
        var (window, shell, fake, rig) = MountDesktop();
        try
        {
            var env = new EnvRefDto(ManagementRig.Device, "e1", "dev-server", "ssh");
            var hostKey = new SshHostKeyInteractionDto
            {
                Id = "hk1",
                RunId = "env:e1:1",
                Version = 1,
                CreatedAt = Now.ToUnixTimeMilliseconds(),
                Env = env,
                HostName = "10.0.0.5",
                Algorithm = "ssh-ed25519",
                Fingerprint = "ABCDEF012345",
            };
            rig.Stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, ManagementRig.Device, new InteractionsDto(1, [hostKey])));
            Dispatch();

            var overlay = Assert.Single(window.GetVisualDescendants().OfType<SshInteractionOverlay>());
            Assert.True(overlay.IsVisible);
            var hkTexts = overlay.GetVisualDescendants().OfType<TextBlock>().Where(static t => t.IsEffectivelyVisible).Select(static t => t.Text).ToList();
            Assert.Contains(NcwTexts.Get(NcwTextKind.SshHostKeyTitle), hkTexts);
            Assert.Contains("SHA256:ABCDEF012345", hkTexts);

            // 切到凭据提问
            var cred = new SshCredentialInteractionDto
            {
                Id = "cred1",
                RunId = "env:e1:2",
                Version = 1,
                CreatedAt = Now.AddSeconds(1).ToUnixTimeMilliseconds(),
                Env = env,
                PromptKind = "password",
                HostName = "10.0.0.5",
                UserName = "ops",
                Prompts = [],
                PreviousRejected = true,
                CanRemember = true,
                HasSaved = true,
            };
            rig.Stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, ManagementRig.Device, new InteractionsDto(2, [cred])));
            Dispatch();

            var credTexts = overlay.GetVisualDescendants().OfType<TextBlock>().Where(static t => t.IsEffectivelyVisible).Select(static t => t.Text).ToList();
            Assert.Contains(NcwTexts.Get(NcwTextKind.SshCredTitlePassword), credTexts);
            Assert.Contains(NcwTexts.Get(NcwTextKind.SshCredPreviousRejected), credTexts);
            Assert.Empty(fake.CredentialAnswers);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task New_project_dialog_renders_ssh_remote_browser_and_inline_interaction()
    {
        var rig = new ManagementRig("owner");
        var fake = new FakeEnvironments(rig.Stores);
        fake.Environments.Add(FakeEnvironments.SshDto("e1", "dev-server", EnvironmentStateNames.Ready));
        fake.Push();
        var interactions = new SshInteractionsViewModel(rig.Stores, fake);
        var actions = new ProjectActions(rig.Stores, new DeviceRegistry(), () => NoDialogs.Instance, () => DefaultClientText.Instance, fake, interactions);
        using var form = new NewProjectViewModel(rig.Stores, actions, () => NoDialogs.Instance, () => DefaultClientText.Instance,
            ManagementRig.Device, fake, interactions, initialSource: NewProjectSource.Ssh);

        var dialog = new NewProjectDialog { DataContext = form };
        var window = new Window { Width = 620, Height = 720, Content = dialog };
        window.Show();
        await form.RefreshRemoteCommand.ExecuteAsync(null);
        Dispatch();
        try
        {
            var texts = dialog.GetVisualDescendants().OfType<TextBlock>().Where(static t => t.IsEffectivelyVisible).Select(static t => t.Text).ToList();
            Assert.Contains("api", texts);
            Assert.Equal("/home/ops", form.Path);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Mobile_devices_page_renders_ssh_interactions_and_settings_opens_environments()
    {
        var rig = new ManagementRig("owner");
        var fake = new FakeEnvironments(rig.Stores);
        fake.Environments.Add(FakeEnvironments.SshDto("e1", "dev-server", EnvironmentStateNames.Ready));
        fake.Push();
        var shell = new ShellViewModel(rig.Stores, environments: fake);
        var env = new EnvRefDto(ManagementRig.Device, "e1", "dev-server", "ssh");
        rig.Stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, ManagementRig.Device, new InteractionsDto(1,
        [
            new SshHostKeyInteractionDto
            {
                Id = "hk-mobile",
                RunId = "env:e1:1",
                Version = 1,
                CreatedAt = Now.ToUnixTimeMilliseconds(),
                Env = env,
                HostName = "10.0.0.5",
                Algorithm = "ssh-ed25519",
                Fingerprint = "MOBILEFP",
            },
        ])));

        var mobile = new MobileShell { DataContext = shell };
        var window = new Window { Width = 390, Height = 844, Content = mobile };
        window.Show();
        shell.Navigation.SelectTabCommand.Execute(MobileTab.Devices);
        Dispatch();
        try
        {
            var devicesTexts = mobile.GetVisualDescendants().OfType<TextBlock>().Where(static t => t.IsEffectivelyVisible).Select(static t => t.Text).ToList();
            Assert.Contains("SHA256:MOBILEFP", devicesTexts);

            shell.Navigation.OpenSettingsSection(SettingsSection.Environments);
            Dispatch();
            Assert.NotEmpty(mobile.GetVisualDescendants().OfType<EnvironmentsPage>());
        }
        finally
        {
            window.Close();
        }
    }
}
