namespace Ncw.Agent.Storage.Takeover;

/// <summary>「回退到 2.x」能不能做(设置 · 数据)。<see cref="TakenOverAt"/> 是接管提交的时刻(Unix 毫秒)。</summary>
public sealed record RollbackAvailability(bool Available, long? TakenOverAt, bool Pending);

/// <summary>
/// 设置 · 数据 · 回退到 2.x(docs/design/04 §5)。回退本身是 <see cref="DataRootTakeover.Rollback"/>,它要求<b>数据库已经关闭</b>;
/// 而 Agent 运行时一直开着库,所以这里分两步:
/// <list type="number">
/// <item><see cref="Request"/>:控制端确认后,在数据根里留一个请求文件(原子写)。控制端随后让 Agent 优雅退出、自己退出。</item>
/// <item><see cref="RunPending"/>:Agent 关库之后(优雅退出的最后一步),或者下次启动、开库之前(上次被强杀没做完),执行回退并删掉请求文件。</item>
/// </list>
/// 回退之后日志停在 <see cref="TakeoverPhase.RolledBack"/>:下次启动 3.0 <b>不自动</b>再接管(否则刚回到 2.x 的用户误开一次 3.0 数据就又被拿走),
/// 要用户在 3.0 里明确点「重新迁移」。
/// </summary>
public static class RollbackToLegacy
{
    public const string RequestFile = "rollback-to-2x.request";

    /// <summary>只有「从 2.x 接管过、并且接管前的备份还在」的数据根能回退;全新的 3.0 数据根没有可回退的东西。</summary>
    public static RollbackAvailability Inspect(string dataRoot)
    {
        var pending = File.Exists(Path.Combine(dataRoot, RequestFile));
        TakeoverJournal? journal;
        try
        {
            journal = DataRootTakeover.ReadJournal(dataRoot);
        }
        catch (TakeoverException)
        {
            return new RollbackAvailability(false, null, pending);
        }

        var available = journal is { Phase: TakeoverPhase.Committed }
            && File.Exists(Path.Combine(dataRoot, journal.BackupDir, AgentDatabase.FileName));
        return new RollbackAvailability(available, available ? journal!.CommittedAt : null, pending);
    }

    /// <summary>登记一次回退请求。不能回退时抛 <see cref="TakeoverFailure.NothingToRollBack"/>。幂等。</summary>
    public static void Request(string dataRoot, long now)
    {
        if (!Inspect(dataRoot).Available)
        {
            throw new TakeoverException(TakeoverFailure.NothingToRollBack, "This data was not taken over from 2.x, or the pre-takeover backup is gone.");
        }

        var path = Path.Combine(dataRoot, RequestFile);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, now.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>撤回还没执行的请求(用户改主意了)。</summary>
    public static void Cancel(string dataRoot) => File.Delete(Path.Combine(dataRoot, RequestFile));

    /// <summary>
    /// 有请求就执行回退(调用前数据库必须已经关闭)。返回日志;没有请求时返回 null。
    /// 成功后删掉请求文件;失败时:2.x 在运行等「之后能成功」的原因保留请求(下次再试),备份没了等「永远不会成功」的原因删掉请求,异常照常抛出。
    /// </summary>
    public static TakeoverJournal? RunPending(string dataRoot, TakeoverOptions? options = null)
    {
        var path = Path.Combine(dataRoot, RequestFile);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // 用户在确认框里已经明确接受「迁移之后在 3.0 里写的数据不会带回 2.x」;那份库会另存为 post-takeover.db,不会丢
            var journal = DataRootTakeover.Rollback(dataRoot, acknowledgeDataLoss: true, options);
            File.Delete(path);
            return journal;
        }
        catch (TakeoverException e) when (e.Failure is TakeoverFailure.NothingToRollBack or TakeoverFailure.BackupInvalid)
        {
            File.Delete(path);
            throw;
        }
    }

    /// <summary>这份数据刚被回退到 2.x(日志停在 rolled-back,库里没有所有权标记):3.0 启动时不自动再接管。</summary>
    public static bool IsRolledBack(string dataRoot)
    {
        try
        {
            return DataRootTakeover.ReadJournal(dataRoot) is { Phase: TakeoverPhase.RolledBack };
        }
        catch (TakeoverException)
        {
            return false;
        }
    }
}
