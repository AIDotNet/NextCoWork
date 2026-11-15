using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Ncw.Agent.Environments;

/// <summary>
/// 把零散的文件变化按静默窗口合并成批,并对同一路径的连续变化做合并(本机监视器和 SSH 轮询器共用)。
/// 合并规则:新建后又修改仍是「新建」;新建后又删除互相抵消;修改后删除是「删除」;删除后又新建是「修改」。
/// </summary>
internal sealed class FsChangeBatcher
{
    private readonly Channel<FsChange> _channel = Channel.CreateUnbounded<FsChange>(new UnboundedChannelOptions { SingleReader = true });

    public void Add(FsChange change) => _channel.Writer.TryWrite(change);

    public void Complete() => _channel.Writer.TryComplete();

    public async IAsyncEnumerable<IReadOnlyList<FsChange>> ReadBatchesAsync(TimeSpan debounce, [EnumeratorCancellation] CancellationToken ct)
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            var pending = new Dictionary<string, FsChange>(StringComparer.Ordinal);
            var order = new List<string>();
            Drain(reader, pending, order);

            // 静默窗口:最后一次变化之后 debounce 内没有新变化才送出
            for (;;)
            {
                using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quiet.CancelAfter(debounce);
                bool more;
                try
                {
                    more = await reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    break;
                }

                if (!more)
                {
                    break;
                }

                Drain(reader, pending, order);
            }

            var batch = order.Where(pending.ContainsKey).Select(k => pending[k]).ToList();
            if (batch.Count > 0)
            {
                yield return batch;
            }
        }
    }

    private static void Drain(ChannelReader<FsChange> reader, Dictionary<string, FsChange> pending, List<string> order)
    {
        while (reader.TryRead(out var change))
        {
            var key = change.Path.Value;
            if (!pending.TryGetValue(key, out var previous))
            {
                pending[key] = change;
                order.Add(key);
                continue;
            }

            var merged = (previous.Kind, change.Kind) switch
            {
                (FsChangeKind.Created, FsChangeKind.Modified) => previous,
                (FsChangeKind.Created, FsChangeKind.Deleted) => null,
                (FsChangeKind.Modified, FsChangeKind.Deleted) => change,
                (FsChangeKind.Deleted, FsChangeKind.Created) => change with { Kind = FsChangeKind.Modified },
                _ => change,
            };
            if (merged is null)
            {
                pending.Remove(key);
            }
            else
            {
                pending[key] = merged;
            }
        }
    }
}
