using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Devices;

/// <summary>
/// 一条连接上的「模型目录 / 供应商」镜像同步(<see cref="DeviceLink"/> 每次连上后调 <see cref="AttachAsync"/>):
/// 订阅 <c>catalog</c>(viewer 起)、<c>providers</c>(member 起)、<c>providers.oauth</c>(owner),把全量状态折进 <see cref="ClientStores.Catalog"/> / <see cref="ClientStores.Providers"/>;
/// 目录版本或供应商版本变了就按需重拉:目录的供应商列表(小)和模型选择器候选(<c>providers.models.list</c>,别名 + 目录 + 绑定规则只在设备上算一份)。
/// 设备不支持某个能力(老设备)或角色不够就跳过,不报错。设备的事实只经这里写入这两个 Store(规则 1)。
/// </summary>
internal sealed class DeviceConfigSync(ClientStores stores, IUiDispatcher ui, Func<string> deviceId)
{
    private int _optionsRunning;
    private int _optionsDirty;
    private int _catalogProvidersRunning;
    private int _catalogProvidersDirty;

    public async Task AttachAsync(DeviceClient client, CancellationToken ct)
    {
        var role = client.Welcome.Role;
        var features = client.Welcome.Features;
        if (features.Contains(Capabilities.Catalog))
        {
            await client.SubscribeAsync(CatalogMethods.Topic,
                (state, _) => OnCatalog(client, state, ct),
                evt =>
                {
                    if (evt.Type == CatalogEvents.Changed)
                    {
                        OnCatalog(client, FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<CatalogTopicState>()), ct);
                    }
                }, ct).ConfigureAwait(false);
        }

        if (features.Contains(Capabilities.Providers) && role is "member" or "owner")
        {
            await client.SubscribeAsync(ProvidersMethods.Topic,
                (state, _) => OnProviders(client, state, ct),
                evt =>
                {
                    switch (evt.Type)
                    {
                        case ProvidersEvents.Changed:
                            OnProviders(client, FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<ProvidersTopicState>()), ct);
                            break;
                        case ProvidersEvents.AccountsChanged:
                            var accounts = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<ProviderAccountsDto>());
                            ui.Post(() => stores.Providers.Update(m => ConfigReducer.ApplyAccounts(m, deviceId(), accounts)));
                            break;
                    }
                }, ct).ConfigureAwait(false);
        }

        if (features.Contains(Capabilities.Providers) && role is "owner")
        {
            await client.SubscribeAsync(ProvidersMethods.OAuthTopic,
                (state, _) => ui.Post(() => stores.Providers.Update(m => ConfigReducer.ApplyOAuth(m, deviceId(), state.Current))),
                evt =>
                {
                    if (evt.Type == ProvidersEvents.OAuthProgress)
                    {
                        var progress = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<OAuthProgressDto>());
                        ui.Post(() => stores.Providers.Update(m => ConfigReducer.ApplyOAuth(m, deviceId(), progress)));
                    }
                }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>设备断开:目录和供应商配置标为未知(规则 7),不把旧值当现状;正在进行的登录进度清掉。</summary>
    public void MarkUnknown()
    {
        var id = deviceId();
        ui.Post(() =>
        {
            stores.Catalog.Update(m => ConfigReducer.MarkCatalogUnknown(m, id));
            stores.Providers.Update(m => ConfigReducer.MarkProvidersUnknown(m, id));
        });
    }

    private void OnCatalog(DeviceClient client, CatalogTopicState state, CancellationToken ct)
    {
        ui.Post(() => stores.Catalog.Update(m => ConfigReducer.ApplyCatalog(m, deviceId(), state)));
        _ = RefreshCatalogProvidersAsync(client, ct);
        if (client.Welcome.Role is "member" or "owner" && client.Welcome.Features.Contains(Capabilities.Providers))
        {
            _ = RefreshOptionsAsync(client, ct);
        }
    }

    private void OnProviders(DeviceClient client, ProvidersTopicState state, CancellationToken ct)
    {
        ui.Post(() => stores.Providers.Update(m => ConfigReducer.ApplyProviders(m, deviceId(), state)));
        _ = RefreshOptionsAsync(client, ct);
    }

    /// <summary>合并并发:正在拉时再来的触发只标「脏」,拉完再来一轮(最终一定追平最新)。</summary>
    private async Task RefreshCatalogProvidersAsync(DeviceClient client, CancellationToken ct)
    {
        Volatile.Write(ref _catalogProvidersDirty, 1);
        if (Interlocked.CompareExchange(ref _catalogProvidersRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (Interlocked.Exchange(ref _catalogProvidersDirty, 0) == 1 && !ct.IsCancellationRequested)
            {
                var result = await client.CallAsync(CatalogMethods.Providers, Unit.Value, ct).ConfigureAwait(false);
                ui.Post(() => stores.Catalog.Update(m => ConfigReducer.ApplyCatalogProviders(m, deviceId(), result)));
            }
        }
        catch (Exception e) when (e is ProtocolException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 断线了:连接恢复后订阅会再给一份快照,重新触发。
        }
        finally
        {
            Volatile.Write(ref _catalogProvidersRunning, 0);
        }

        if (Volatile.Read(ref _catalogProvidersDirty) == 1 && !ct.IsCancellationRequested)
        {
            _ = RefreshCatalogProvidersAsync(client, ct);
        }
    }

    private async Task RefreshOptionsAsync(DeviceClient client, CancellationToken ct)
    {
        Volatile.Write(ref _optionsDirty, 1);
        if (Interlocked.CompareExchange(ref _optionsRunning, 1, 0) != 0)
        {
            return;
        }

        try
        {
            while (Interlocked.Exchange(ref _optionsDirty, 0) == 1 && !ct.IsCancellationRequested)
            {
                // 候选是「供应商快照 + 目录」算出来的:拉之前先记下是哪一对版本,到达时若镜像已经更新就丢弃(下一轮会补)。
                var (providers, catalog) = await ReadVersionsAsync().ConfigureAwait(false);
                var options = await client.CallAsync(ProvidersMethods.ModelsList, new ModelsListParams(), ct).ConfigureAwait(false);
                ui.Post(() => stores.Providers.Update(m => ConfigReducer.ApplyOptions(m, deviceId(), options, providers, catalog)));
            }
        }
        catch (Exception e) when (e is ProtocolException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 同上。
        }
        finally
        {
            Volatile.Write(ref _optionsRunning, 0);
        }

        if (Volatile.Read(ref _optionsDirty) == 1 && !ct.IsCancellationRequested)
        {
            _ = RefreshOptionsAsync(client, ct);
        }
    }

    /// <summary>Store 只能在 UI 线程上读:经调度器取一份当前的(供应商版本, 目录版本)。</summary>
    private Task<(long Providers, long Catalog)> ReadVersionsAsync()
    {
        var tcs = new TaskCompletionSource<(long, long)>(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Post(() =>
        {
            var id = deviceId();
            tcs.TrySetResult((stores.Providers.State.Of(id).Version, stores.Catalog.State.Of(id).Status.Version));
        });
        return tcs.Task;
    }
}
