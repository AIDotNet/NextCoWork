using Ncw.Agent.Environments;
using Ncw.Agent.Environments.Ssh;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host.Environments;

internal sealed partial class EnvironmentService
{
    private EnvironmentDto ToDto(EnvironmentDescriptor descriptor, string defaultId)
    {
        var isDefault = descriptor.Id == defaultId;
        if (descriptor.Kind == EnvironmentKind.Local)
        {
            var local = _registry.Get(descriptor.Id);
            return new EnvironmentDto(descriptor.Id, descriptor.Name, EnvironmentKindNames.Local, EnvironmentStateNames.Ready, local.Generation, isDefault, true, 0, Facts: FactsOf(local));
        }

        ConnectionView? view;
        lock (_lock)
        {
            view = _views.GetValueOrDefault(descriptor.Id);
        }

        var instance = _registry.TryGetInstance(descriptor.Id);
        var state = instance?.State switch
        {
            EnvironmentState.Connecting => EnvironmentStateNames.Connecting,
            EnvironmentState.Ready => EnvironmentStateNames.Ready,
            EnvironmentState.Reconnecting => EnvironmentStateNames.Reconnecting,
            EnvironmentState.Failed => EnvironmentStateNames.Failed,
            _ => EnvironmentStateNames.Idle,
        };
        EnvironmentErrorDto? error = null;
        if (state == EnvironmentStateNames.Failed)
        {
            var code = _lastErrors.GetValueOrDefault(descriptor.Id, EnvironmentErrorCode.Disconnected);
            error = new EnvironmentErrorDto(
                EnvironmentErrors.KeyOf(code),
                code == EnvironmentErrorCode.HostKeyChanged && _mismatches.TryGetValue(descriptor.Id, out var mismatch) ? mismatch : null);
        }

        var connected = state is EnvironmentStateNames.Ready or EnvironmentStateNames.Reconnecting;
        return new EnvironmentDto(
            descriptor.Id,
            descriptor.Name,
            EnvironmentKindNames.Ssh,
            state,
            instance?.Generation ?? 0,
            isDefault,
            view?.Enabled ?? true,
            view?.Revision ?? 0,
            error,
            connected ? _latencies.TryGetValue(descriptor.Id, out var latency) ? latency : null : null,
            view is null ? null : new SshTargetDto(view.TargetKind, view.Host, view.Port, view.Username, view.IdentityFile is { Length: > 0 }, view.ProxyJump, view.ConfigFile is { Length: > 0 }),
            view is null ? null : EnvironmentProfiles.AuthName(view.AuthMethod),
            view is null ? null : EnvironmentProfiles.WirePlatformName(view.Platform),
            view?.Proxy is { } proxy ? new SshProxyDto(proxy.Kind, proxy.Host, proxy.Port, proxy.Username, _secrets.Has(ConnectionSecrets.ProxyPasswordRef(descriptor.Id))) : null,
            _secrets.Has(ConnectionSecrets.PasswordRef(descriptor.Id)),
            connected && instance is not null ? FactsOf(instance) : null);
    }

    private static EnvironmentFactsDto? FactsOf(IExecutionEnvironment environment)
    {
        try
        {
            var facts = environment.Facts;
            return new EnvironmentFactsDto(
                facts.Os switch { EnvironmentOs.Windows => "windows", EnvironmentOs.MacOS => "macos", _ => "linux" },
                facts.Architecture, facts.Hostname, facts.Username);
        }
        catch (EnvironmentException)
        {
            return null; // 还没连上过:没有事实
        }
    }
}
