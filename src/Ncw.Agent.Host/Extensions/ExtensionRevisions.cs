using Ncw.Protocol.Methods;
using Ncw.Protocol.Session;

namespace Ncw.Agent.Host.Extensions;

/// <summary>
/// <c>extensions</c> 主题的状态:Skills / 命令 / Hooks 三个域各一个单调修订号(设备上每次成功改动加一)。首帧(<see cref="State"/>)和增量是同一个类型、同一个生成函数(规则 5);
/// 修订号递增与推送在同一把锁里,所以版本序就是推送序(和 <c>goal</c> 主题同理)。
/// </summary>
internal sealed class ExtensionRevisions(TopicHub topics)
{
    private readonly Lock _gate = new();
    private long _skills;
    private long _commands;
    private long _hooks;

    /// <summary>
    /// ★ 不取 <c>_gate</c>:主题表在持有它自己的锁时调快照函数,而发布路径是先 <c>_gate</c> 后主题表的锁——两把锁反着取就是死锁。
    /// 三个计数各自单调,控制端按域取「不更旧」的(逐域取最大),所以读到略旧的某一域只会被随后的事件追平。
    /// </summary>
    public ExtensionsTopicState State() =>
        new(Volatile.Read(ref _skills), Volatile.Read(ref _commands), Volatile.Read(ref _hooks));

    public void BumpSkills() => Bump(ref _skills);

    public void BumpCommands() => Bump(ref _commands);

    public void BumpHooks() => Bump(ref _hooks);

    private void Bump(ref long counter)
    {
        lock (_gate)
        {
            counter++;
            topics.Publish(ExtensionsMethods.Topic.Name, ExtensionsEvents.Changed, new ExtensionsTopicState(_skills, _commands, _hooks), PayloadShapes.GetShape<ExtensionsTopicState>());
        }
    }
}
