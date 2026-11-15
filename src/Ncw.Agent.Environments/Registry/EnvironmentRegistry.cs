using Ncw.Agent.Environments.Ssh;

namespace Ncw.Agent.Environments;

/// <summary>环境的配置(可持久化的那部分):id、名字、种类,以及 SSH 环境的连接配置。运行时实例由 <see cref="EnvironmentRegistry"/> 按需创建。</summary>
public sealed record EnvironmentDescriptor(string Id, string Name, EnvironmentKind Kind, SshEnvironmentProfile? Ssh = null);

public sealed class EnvironmentRegistryOptions
{
    public LocalEnvironmentOptions? Local { get; init; }

    /// <summary>SSH 环境依赖的设备侧服务(指纹存储、确认回调、凭据)。没有它就不能添加 SSH 环境。</summary>
    public SshEnvironmentServices? SshServices { get; init; }

    public SshEnvironmentOptions? SshOptions { get; init; }

    /// <summary>覆盖 SSH 环境实例的创建(测试用)。</summary>
    public Func<EnvironmentId, EnvironmentDescriptor, IExecutionEnvironment>? SshFactory { get; init; }
}

public enum EnvironmentRegistryChange
{
    Added,
    Updated,
    Removed,
    DefaultChanged,
}

public sealed class EnvironmentRegistryChangedEventArgs(EnvironmentRegistryChange change, string id) : EventArgs
{
    public EnvironmentRegistryChange Change { get; } = change;

    public string Id { get; } = id;
}

/// <summary>
/// 一台设备上的环境列表和默认环境(06 §6 <c>environments.list / create / update / delete</c> 的设备侧实现)。
/// <list type="bullet">
/// <item><c>local</c> 永远存在、不能删除或改名;</item>
/// <item>默认环境初始是 <c>local</c>;删掉默认环境后回到 <c>local</c>;</item>
/// <item>每个环境只有一个运行时实例(复用连接),惰性创建;<see cref="Update"/> 和 <see cref="Remove"/> 会关闭旧实例;</item>
/// <item>不依赖协议层、存储层和界面:持久化由宿主通过 <see cref="Snapshot"/> 与 <see cref="Load"/> 完成。</item>
/// </list>
/// </summary>
public sealed class EnvironmentRegistry : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly EnvironmentRegistryOptions _options;
    private readonly Dictionary<string, EnvironmentDescriptor> _descriptors = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, IExecutionEnvironment> _instances = new(StringComparer.Ordinal);
    private string _defaultId = EnvironmentId.LocalName;
    private bool _disposed;

    public EnvironmentRegistry(string deviceId, EnvironmentRegistryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        DeviceId = deviceId;
        _options = options ?? new EnvironmentRegistryOptions();
        var local = new EnvironmentDescriptor(EnvironmentId.LocalName, _options.Local?.DisplayName ?? "Local", EnvironmentKind.Local);
        _descriptors[local.Id] = local;
        _order.Add(local.Id);
    }

    public string DeviceId { get; }

    public event EventHandler<EnvironmentRegistryChangedEventArgs>? Changed;

    /// <summary>任何一个已创建的环境实例的状态变化(转发)。</summary>
    public event EventHandler<EnvironmentStatusChangedEventArgs>? EnvironmentStatusChanged;

    public string DefaultId
    {
        get
        {
            lock (_gate)
            {
                return _defaultId;
            }
        }
    }

    /// <summary>环境列表,<c>local</c> 在最前,其余按添加顺序。</summary>
    public IReadOnlyList<EnvironmentDescriptor> List()
    {
        lock (_gate)
        {
            return [.. _order.Select(id => _descriptors[id])];
        }
    }

    public EnvironmentDescriptor? Find(string id)
    {
        lock (_gate)
        {
            return _descriptors.GetValueOrDefault(id);
        }
    }

    public EnvironmentDescriptor AddSsh(string id, string name, SshEnvironmentProfile profile)
    {
        ValidateId(id);
        if (_options.SshServices is null && _options.SshFactory is null)
        {
            throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "no SSH services configured on this device");
        }

        var descriptor = new EnvironmentDescriptor(id, name, EnvironmentKind.Ssh, profile);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_descriptors.ContainsKey(id))
            {
                throw new EnvironmentException(EnvironmentErrorCode.Conflict, $"environment '{id}' already exists");
            }

            _descriptors[id] = descriptor;
            _order.Add(id);
        }

        Changed?.Invoke(this, new EnvironmentRegistryChangedEventArgs(EnvironmentRegistryChange.Added, id));
        return descriptor;
    }

    /// <summary>更新 SSH 环境的名字或连接配置。已创建的实例会被关闭,下次使用时按新配置重新连接。</summary>
    public async Task<EnvironmentDescriptor> UpdateSshAsync(string id, string name, SshEnvironmentProfile profile)
    {
        IExecutionEnvironment? old;
        EnvironmentDescriptor descriptor;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_descriptors.TryGetValue(id, out var existing) || existing.Kind != EnvironmentKind.Ssh)
            {
                throw new EnvironmentException(EnvironmentErrorCode.EnvironmentNotFound, id);
            }

            descriptor = existing with { Name = name, Ssh = profile };
            _descriptors[id] = descriptor;
            _instances.Remove(id, out old);
        }

        if (old is not null)
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }

        Changed?.Invoke(this, new EnvironmentRegistryChangedEventArgs(EnvironmentRegistryChange.Updated, id));
        return descriptor;
    }

    /// <summary>删除环境并关闭它的实例。<c>local</c> 不能删除。删掉默认环境后默认回到 <c>local</c>。</summary>
    public async Task RemoveAsync(string id)
    {
        IExecutionEnvironment? old;
        var defaultReset = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (id == EnvironmentId.LocalName)
            {
                throw new EnvironmentException(EnvironmentErrorCode.Unsupported, "the local environment cannot be removed");
            }

            if (!_descriptors.Remove(id))
            {
                throw new EnvironmentException(EnvironmentErrorCode.EnvironmentNotFound, id);
            }

            _order.Remove(id);
            _instances.Remove(id, out old);
            if (_defaultId == id)
            {
                _defaultId = EnvironmentId.LocalName;
                defaultReset = true;
            }
        }

        if (old is not null)
        {
            old.StatusChanged -= OnStatusChanged;
            await old.DisposeAsync().ConfigureAwait(false);
        }

        Changed?.Invoke(this, new EnvironmentRegistryChangedEventArgs(EnvironmentRegistryChange.Removed, id));
        if (defaultReset)
        {
            Changed?.Invoke(this, new EnvironmentRegistryChangedEventArgs(EnvironmentRegistryChange.DefaultChanged, EnvironmentId.LocalName));
        }
    }

    public void SetDefault(string id)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_descriptors.ContainsKey(id))
            {
                throw new EnvironmentException(EnvironmentErrorCode.EnvironmentNotFound, id);
            }

            if (_defaultId == id)
            {
                return;
            }

            _defaultId = id;
        }

        Changed?.Invoke(this, new EnvironmentRegistryChangedEventArgs(EnvironmentRegistryChange.DefaultChanged, id));
    }

    /// <summary>取环境实例;<paramref name="id"/> 为空取默认环境。实例惰性创建、复用。</summary>
    public IExecutionEnvironment Get(string? id = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var key = string.IsNullOrEmpty(id) ? _defaultId : id;
            if (_instances.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (!_descriptors.TryGetValue(key, out var descriptor))
            {
                throw new EnvironmentException(EnvironmentErrorCode.EnvironmentNotFound, key);
            }

            var instance = Create(descriptor);
            instance.StatusChanged += OnStatusChanged;
            _instances[key] = instance;
            return instance;
        }
    }

    /// <summary>已经创建的运行时实例(不创建):没有 = 这个环境还没被用过,或连接已被 <see cref="DisconnectAsync"/> 回收,状态等同 <see cref="EnvironmentState.Idle"/>。</summary>
    public IExecutionEnvironment? TryGetInstance(string id)
    {
        lock (_gate)
        {
            return _instances.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// 断开并丢弃一个环境的运行时实例(空闲回收、用户点「断开」):配置和默认环境不动,下次 <see cref="Get"/> 惰性重新创建、重新连接。
    /// 实例上的凭据缓存随之清掉(下次连接会重新取:已保存的直接用,没保存的重新问)。<c>local</c> 没有连接可断,什么都不做。返回是否真的关了一个实例。
    /// </summary>
    public async Task<bool> DisconnectAsync(string id)
    {
        IExecutionEnvironment? old;
        lock (_gate)
        {
            if (_disposed || id == EnvironmentId.LocalName)
            {
                return false;
            }

            _instances.Remove(id, out old);
        }

        if (old is null)
        {
            return false;
        }

        old.StatusChanged -= OnStatusChanged;
        await old.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>当前的持久化快照:除 <c>local</c> 之外的环境配置和默认环境 id。</summary>
    public (IReadOnlyList<EnvironmentDescriptor> Environments, string DefaultId) Snapshot()
    {
        lock (_gate)
        {
            return ([.. _order.Where(id => id != EnvironmentId.LocalName).Select(id => _descriptors[id])], _defaultId);
        }
    }

    /// <summary>从持久化的配置恢复(启动时调用一次)。默认环境不存在时退回 <c>local</c>;重复或非法的 id 被跳过。</summary>
    public void Load(IEnumerable<EnvironmentDescriptor> environments, string? defaultId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var descriptor in environments)
            {
                if (descriptor.Kind != EnvironmentKind.Ssh || descriptor.Ssh is null || !IsValidId(descriptor.Id) || _descriptors.ContainsKey(descriptor.Id))
                {
                    continue;
                }

                _descriptors[descriptor.Id] = descriptor;
                _order.Add(descriptor.Id);
            }

            _defaultId = defaultId is not null && _descriptors.ContainsKey(defaultId) ? defaultId : EnvironmentId.LocalName;
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<IExecutionEnvironment> instances;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            instances = [.. _instances.Values];
            _instances.Clear();
        }

        foreach (var instance in instances)
        {
            instance.StatusChanged -= OnStatusChanged;
            await instance.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IExecutionEnvironment Create(EnvironmentDescriptor descriptor)
    {
        var id = new EnvironmentId(DeviceId, descriptor.Id);
        if (descriptor.Kind == EnvironmentKind.Local)
        {
            return new LocalEnvironment(DeviceId, _options.Local);
        }

        if (_options.SshFactory is { } factory)
        {
            return factory(id, descriptor);
        }

        // 显示名以环境条目的名字为准(配置里的 Name 只是创建时填的默认值)
        var profile = descriptor.Ssh ?? throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, descriptor.Id);
        return new SshEnvironment(id, profile with { Name = descriptor.Name }, _options.SshServices!, _options.SshOptions);
    }

    private void OnStatusChanged(object? sender, EnvironmentStatusChangedEventArgs e) => EnvironmentStatusChanged?.Invoke(sender, e);

    private static void ValidateId(string id)
    {
        if (!IsValidId(id))
        {
            throw new EnvironmentException(EnvironmentErrorCode.InvalidProfile, "environment id");
        }
    }

    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= 128 && id != EnvironmentId.LocalName && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EnvironmentRegistry));
        }
    }
}
