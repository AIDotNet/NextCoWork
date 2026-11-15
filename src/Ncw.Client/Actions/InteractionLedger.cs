using System.Collections.Concurrent;

namespace Ncw.Client.Actions;

/// <summary>
/// 「这个控制端自己回答过哪些交互」的记录。收件箱据此区分一条待办的消失:自己答的,还是被另一台设备抢先处理了
/// (后者要显示「已在另一台设备上处理」)。纯本机内存状态:事实仍在设备上(交互的结局以设备为准),这里只是本机界面的一个旁证,重启后丢掉没有影响。
/// 条数有上限,超出后清掉最早的一批。
/// </summary>
public sealed class InteractionLedger
{
    private const int Capacity = 512;

    private readonly ConcurrentDictionary<string, long> _answered = new(StringComparer.Ordinal);
    private long _counter;

    public void MarkAnswered(string interactionId)
    {
        _answered[interactionId] = Interlocked.Increment(ref _counter);
        if (_answered.Count > Capacity)
        {
            foreach (var key in _answered.OrderBy(static kv => kv.Value).Take(Capacity / 4).Select(static kv => kv.Key))
            {
                _answered.TryRemove(key, out _);
            }
        }
    }

    public bool WasAnsweredHere(string interactionId) => _answered.ContainsKey(interactionId);
}
