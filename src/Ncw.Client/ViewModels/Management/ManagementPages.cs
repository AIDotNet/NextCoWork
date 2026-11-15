using Ncw.Client.Actions;
using Ncw.Client.Services;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>设置页里「设备 / 项目」作用域的各个页面,共用一个 <see cref="SettingsScope"/>(设备切换器和项目切换器是同一份纯 UI 状态)。</summary>
public sealed class ManagementPages
{
    public ManagementPages(
        ClientStores stores,
        ISettingsCommands settings,
        ISkillsCommands skills,
        ICommandsAdminCommands commands,
        IHooksCommands hooks,
        IMcpCommands mcp,
        IUsageCommands usage,
        IStorageCommands storage,
        IGatewayCommands gateway,
        Func<IDialogService> dialogs,
        Func<IUrlLauncher> launcher,
        TimeProvider clock,
        IPluginsCommands? plugins = null)
    {
        Scope = new SettingsScope(stores);
        General = new GeneralSettingsViewModel(Scope, stores, settings);
        Project = new ProjectSettingsViewModel(Scope, stores, settings);
        Skills = new SkillsViewModel(Scope, stores, skills, dialogs);
        Commands = new CommandsViewModel(Scope, stores, commands);
        Hooks = new HooksViewModel(Scope, stores, hooks);
        Mcp = new McpViewModel(Scope, stores, mcp);
        Usage = new UsageViewModel(Scope, usage, clock);
        Storage = new StorageViewModel(Scope, stores, storage, settings, dialogs, clock);
        Gateway = new GatewayViewModel(Scope, stores, gateway, settings, launcher);
        Plugins = new PluginsViewModel(Scope, stores, plugins ?? UnavailablePluginsCommands.Instance, dialogs);
    }

    public SettingsScope Scope { get; }

    public GeneralSettingsViewModel General { get; }

    public ProjectSettingsViewModel Project { get; }

    public SkillsViewModel Skills { get; }

    public CommandsViewModel Commands { get; }

    public HooksViewModel Hooks { get; }

    public McpViewModel Mcp { get; }

    public UsageViewModel Usage { get; }

    public StorageViewModel Storage { get; }

    public GatewayViewModel Gateway { get; }

    public PluginsViewModel Plugins { get; }

    /// <summary>切到某个页面:第一次打开时加载;离开网关页时清掉内存里的密钥。</summary>
    public Task ActivateAsync(SettingsSection section)
    {
        if (section != SettingsSection.Gateway)
        {
            Gateway.HideKeys();
        }

        return section switch
        {
            SettingsSection.General => General.ActivateAsync(),
            SettingsSection.Project => Project.ActivateAsync(),
            SettingsSection.Skills => Skills.ActivateAsync(),
            SettingsSection.Commands => Commands.ActivateAsync(),
            SettingsSection.Hooks => Hooks.ActivateAsync(),
            SettingsSection.Mcp => Mcp.ActivateAsync(),
            SettingsSection.Usage => Usage.ActivateAsync(),
            SettingsSection.Storage => Storage.ActivateAsync(),
            SettingsSection.Gateway => Gateway.ActivateAsync(),
            SettingsSection.Plugins => Plugins.ActivateAsync(),
            _ => Task.CompletedTask,
        };
    }
}
