using System.Runtime.CompilerServices;
using Ncw.Client.Stores;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;
using Ncw.Protocol.Wire;

namespace Ncw.Client.Devices;

/// <summary>
/// 显示端(终端控件)的接收器。<b>回调都来自网络线程</b>(同一个终端的回调严格按序、不会并发),是否切到 UI 线程由接收器自己决定。
/// 输出是原始字节:多字节字符可能被切在两批之间,显示端要用流式解码器。
/// </summary>
public interface ITerminalSink
{
    /// <summary>
    /// 整个环形缓冲:首次订阅、游标续不上、或设备要求重同步(发送缓冲满)后收到——显示端要<b>重置</b>屏幕再写入它。
    /// </summary>
    /// <param name="data">缓冲内容。</param>
    /// <param name="truncated">缓冲装不下,最早的输出已被丢弃。</param>
    void OnSnapshot(byte[] data, bool truncated);

    /// <summary>一批新输出(游标续上时,是断线期间错过的那些,按序、不重复)。</summary>
    void OnData(byte[] data);

    /// <summary>终态。之后不再有任何回调(也不会再订阅)。</summary>
    void OnExit(int? exitCode, int? signal, string reason);

    /// <summary>终端在设备上已经不存在(关闭、被回收、别的控制端的远程终端)。之后不再有任何回调。</summary>
    void OnGone();
}

/// <summary>
/// 一台设备上「被关注的终端」的订阅管理(<c>terminal/&lt;id&gt;</c> 主题):记着每个终端的接收器和最后收到的序号,
/// 每次(重连后)<see cref="DeviceLink.Synced"/> 对每个还没收口的终端按游标续订——设备补发错过的批,游标续不上就给整个缓冲(接收器重置显示)。
/// 设备发 <c>resync</c> 时 <see cref="DeviceClient"/> 自动不带游标重订,快照照样走 <see cref="ITerminalSink.OnSnapshot"/>。
/// 元数据(状态、退出码)同时折进 <see cref="ClientStores.Terminals"/>(经 <see cref="TerminalReducer"/>,在 UI 线程上);输出字节不进 Store。
/// 每个 <see cref="DeviceLink"/> 一份(弱引用表),由 <see cref="Actions.TerminalActions"/> 取用。
/// </summary>
internal sealed class TerminalWatches
{
    private static readonly ConditionalWeakTable<DeviceLink, TerminalWatches> Table = new();

    private readonly DeviceLink _link;
    private readonly ClientStores _stores;
    private readonly object _gate = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.Ordinal);

    private TerminalWatches(DeviceLink link, ClientStores stores)
    {
        _link = link;
        _stores = stores;
        link.Synced += OnSynced;
    }

    public static TerminalWatches Of(DeviceLink link, ClientStores stores)
    {
        lock (Table)
        {
            if (!Table.TryGetValue(link, out var watches))
            {
                watches = new TerminalWatches(link, stores);
                Table.Add(link, watches);
            }

            return watches;
        }
    }

    /// <summary>开始关注一个终端:同一个终端再次关注会换掉旧的接收器(旧订阅退订)。没连上时先记下,连上后订阅。</summary>
    public async Task AttachAsync(string terminalId, ITerminalSink sink, CancellationToken ct)
    {
        var watch = new Watch(terminalId, sink);
        Watch? previous;
        lock (_gate)
        {
            _watches.TryGetValue(terminalId, out previous);
            _watches[terminalId] = watch;
        }

        if (previous is not null)
        {
            await previous.StopAsync().ConfigureAwait(false);
        }

        if (_link.CurrentClient is { } client)
        {
            await SubscribeAsync(client, watch, ct).ConfigureAwait(false);
        }
    }

    public async Task DetachAsync(string terminalId)
    {
        Watch? watch;
        lock (_gate)
        {
            _watches.Remove(terminalId, out watch);
        }

        if (watch is not null)
        {
            await watch.StopAsync().ConfigureAwait(false);
        }
    }

    private void OnSynced()
    {
        if (_link.CurrentClient is not { } client)
        {
            return;
        }

        Watch[] watches;
        lock (_gate)
        {
            watches = [.. _watches.Values];
        }

        foreach (var watch in watches)
        {
            _ = ReattachAsync(client, watch);
        }
    }

    private async Task ReattachAsync(DeviceClient client, Watch watch)
    {
        try
        {
            await SubscribeAsync(client, watch, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is ProtocolException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // 又断了:下一次 Synced 再来
        }
    }

    private async Task SubscribeAsync(DeviceClient client, Watch watch, CancellationToken ct)
    {
        long? cursor;
        lock (watch.Gate)
        {
            if (watch.Finished || ReferenceEquals(watch.Client, client))
            {
                return; // 收口了,或这条连接上已经订阅过
            }

            watch.Client = client;
            cursor = watch.Cursor;
        }

        try
        {
            var subscription = await client.SubscribeAsync(
                TerminalMethods.Topic(watch.TerminalId),
                (snapshot, seq) => OnSnapshot(watch, snapshot, seq),
                evt => OnEvent(watch, evt),
                ct,
                cursor).ConfigureAwait(false);
            watch.Subscription = subscription;
        }
        catch (ProtocolException e) when (e.Code == ErrorCodes.NotFound)
        {
            Finish(watch, removed: true);
            watch.Sink.OnGone();
        }
        catch
        {
            // 连接问题:下次 Synced 用新连接再来(清掉「这条连接上订阅过」的记号)
            lock (watch.Gate)
            {
                if (ReferenceEquals(watch.Client, client))
                {
                    watch.Client = null;
                }
            }

            throw;
        }
    }

    private void OnSnapshot(Watch watch, TerminalSnapshotDto snapshot, long seq)
    {
        lock (watch.Gate)
        {
            if (watch.Finished)
            {
                return;
            }

            watch.Cursor = seq;
        }

        var terminal = snapshot.Terminal;
        _link.PostToUi(() => _stores.Terminals.Update(m => TerminalReducer.ApplyInfo(m, _link.DeviceId, terminal, seq)));
        watch.Sink.OnSnapshot(snapshot.Data, snapshot.Truncated);
        if (terminal.Status == "exited")
        {
            // 退出事件已经在缓冲的序号之内,不会再作为事件到达
            Finish(watch, removed: false);
            watch.Sink.OnExit(terminal.ExitCode, terminal.Signal, terminal.ExitReason ?? "exited");
        }
    }

    private void OnEvent(Watch watch, EventFrame evt)
    {
        lock (watch.Gate)
        {
            if (watch.Finished)
            {
                return;
            }

            watch.Cursor = evt.Seq;
        }

        switch (evt.Type)
        {
            case TerminalEvents.Data:
                watch.Sink.OnData(FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<TerminalDataDto>()).Data);
                break;
            case TerminalEvents.Exit:
                var exit = FrameCodec.DecodePayload(evt.Data, PayloadShapes.GetShape<TerminalExitDto>());
                var seq = evt.Seq;
                _link.PostToUi(() => _stores.Terminals.Update(m => TerminalReducer.ApplyExit(m, _link.DeviceId, watch.TerminalId, exit, seq)));
                Finish(watch, removed: false);
                watch.Sink.OnExit(exit.ExitCode, exit.Signal, exit.Reason);
                break;
        }
    }

    /// <summary>收口:之后不再回调、不再续订。</summary>
    private void Finish(Watch watch, bool removed)
    {
        lock (watch.Gate)
        {
            watch.Finished = true;
        }

        lock (_gate)
        {
            if (_watches.TryGetValue(watch.TerminalId, out var current) && ReferenceEquals(current, watch))
            {
                _watches.Remove(watch.TerminalId);
            }
        }

        if (removed)
        {
            _link.PostToUi(() => _stores.Terminals.Update(m => TerminalReducer.ApplyRemoved(m, _link.DeviceId, watch.TerminalId)));
        }

        _ = watch.StopAsync();
    }

    private sealed class Watch(string terminalId, ITerminalSink sink)
    {
        public string TerminalId { get; } = terminalId;

        public ITerminalSink Sink { get; } = sink;

        public object Gate { get; } = new();

        /// <summary>最后收到的 seq(快照或事件);续订时作游标。</summary>
        public long? Cursor { get; set; }

        public bool Finished { get; set; }

        /// <summary>订阅所在的连接(同一条连接上不重复订阅)。</summary>
        public DeviceClient? Client { get; set; }

        public IAsyncDisposable? Subscription { get; set; }

        public async Task StopAsync()
        {
            lock (Gate)
            {
                Finished = true;
            }

            if (Subscription is { } subscription)
            {
                Subscription = null;
                try
                {
                    await subscription.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException or ProtocolException)
                {
                    // 连接已经没了
                }
            }
        }
    }
}
