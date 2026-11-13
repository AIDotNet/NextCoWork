using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Takeover;
using Ncw.Protocol.Methods;

namespace Ncw.Agent.Host;

/// <summary>
/// 数据根的准备:全新安装、已接管、或原地接管 2.x 数据(docs/design/04 §4)。状态是 <c>data</c> 主题的真源。
/// <list type="bullet">
/// <item>每次启动都先确认 2.x 没有在用这份数据(2.x 不认识所有权标记,两边同时打开会损坏数据)。</item>
/// <item>全新和已接管的数据根很快,在开始监听之前就准备好(<see cref="TryPrepareFast"/>);
///   需要接管 2.x 数据或者被挡住时,先开始监听,接管在后台进行,控制端通过 <c>data</c> 主题看进度。</item>
/// <item>被挡住(2.x 在运行)或失败后,控制端调用 <c>data.retry</c> 重试;接管提交前的失败已由接管流程自动回退。</item>
/// </list>
/// </summary>
public sealed class DataGate(string dataRoot, string deviceId, TimeProvider clock, TakeoverOptions? takeoverOptions = null) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TakeoverOptions _takeover = takeoverOptions ?? new TakeoverOptions(clock);
    private DataStatus _status = new(DataStates.Preparing, null, null, null);
    private AgentDatabase? _database;

    public DataStatus Status => Volatile.Read(ref _status);

    /// <summary>就绪后的数据库;没就绪时为 null。</summary>
    public AgentDatabase? Database => Volatile.Read(ref _database);

    /// <summary>状态变化(在准备所在的线程上触发)。</summary>
    public event Action<DataStatus>? Changed;

    /// <summary>数据库就绪(先于状态变成 ready 触发,订阅方可以先装配依赖数据库的服务)。</summary>
    public event Action<AgentDatabase>? Ready;

    /// <summary>不需要接管时立即准备好并返回 true;需要接管或被挡住时什么都不做,返回 false。</summary>
    public bool TryPrepareFast()
    {
        try
        {
            LiveOwnerCheck.AssertNone(LiveOwnerCheck.RootsFor(dataRoot), _takeover.IsAlive);
        }
        catch (TakeoverException)
        {
            return false;
        }

        var kind = LegacyLayout.Inspect(dataRoot);
        if (kind == DataRootKind.Legacy || (kind == DataRootKind.Fresh && LegacyLayout.HasUnmigratedLayout(dataRoot)))
        {
            return false;
        }

        // 打开失败(例如库比本程序新)留给 PrepareAsync 报告。
        return Prepare() == DataStates.Ready;
    }

    /// <summary>完整的准备流程(含接管)。已经就绪时直接返回。</summary>
    public async Task<DataStatus> PrepareAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Status.State != DataStates.Ready)
            {
                await Task.Run(Prepare, ct).ConfigureAwait(false);
            }

            return Status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Prepare()
    {
        try
        {
            LiveOwnerCheck.AssertNone(LiveOwnerCheck.RootsFor(dataRoot), _takeover.IsAlive);
            switch (LegacyLayout.Inspect(dataRoot))
            {
                case DataRootKind.Fresh when LegacyLayout.HasUnmigratedLayout(dataRoot):
                    throw new TakeoverException(TakeoverFailure.UnsupportedLayout, "2.x data is in an old layout.");
                case DataRootKind.Fresh:
                {
                    var database = AgentDatabase.Open(dataRoot);
                    try
                    {
                        // 新建的库也写上所有权标记,以后不会被当成 2.x 的数据再接管一次。
                        Ownership.Record(database, new OwnershipRecord(deviceId, "", "", clock.GetUtcNow().ToUnixTimeMilliseconds()));
                    }
                    catch
                    {
                        database.Dispose();
                        throw;
                    }

                    BecomeReady(database, null);
                    break;
                }

                case DataRootKind.Owned:
                    BecomeReady(AgentDatabase.Open(dataRoot), null);
                    break;
                case DataRootKind.Legacy:
                    TakeOver();
                    break;
            }
        }
        catch (TakeoverException e)
        {
            var blocked = e.Failure is TakeoverFailure.LegacyAppRunning or TakeoverFailure.LegacyServiceRunning
                or TakeoverFailure.OwnerUnknown or TakeoverFailure.UnsupportedLayout;
            Fail(blocked ? DataStates.Blocked : DataStates.Failed, ReasonOf(e.Failure), e);
        }
        catch (StorageException e)
        {
            Fail(DataStates.Failed, e.NewerSchema ? DataReasons.NewerSchema : DataReasons.Internal, e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Fail(DataStates.Failed, DataReasons.Internal, e);
        }

        return Status.State;
    }

    private void TakeOver()
    {
        Set(new DataStatus(DataStates.TakingOver, DataSteps.BackingUp, null, null));
        DataRootTakeover.Begin(dataRoot, _takeover);

        Set(new DataStatus(DataStates.TakingOver, DataSteps.Migrating, null, null));
        var database = AgentDatabase.Open(dataRoot);
        try
        {
            Set(new DataStatus(DataStates.TakingOver, DataSteps.Committing, null, null));
            DataRootTakeover.Commit(dataRoot, database, deviceId, _takeover);
        }
        catch
        {
            database.Dispose();
            throw;
        }

        var projects = new WorkspaceRepository(database, clock).List().Count;
        var sessions = database.SessionCount();

        // 遗留 safeStorage 凭据的解密(04 §4 第 4 步)还没实现:数量先报 0,解不开的凭据保持原样。
        BecomeReady(database, new TakeoverSummary(projects, sessions, 0));
    }

    private void BecomeReady(AgentDatabase database, TakeoverSummary? summary)
    {
        Volatile.Write(ref _database, database);
        Ready?.Invoke(database);
        Set(new DataStatus(DataStates.Ready, null, null, summary));
    }

    private void Fail(string state, string reason, Exception e)
    {
        Console.Error.WriteLine($"ncw-agent: data root not ready ({state}/{reason}): {e.Message}");
        Set(new DataStatus(state, null, reason, null));
    }

    private void Set(DataStatus status)
    {
        Volatile.Write(ref _status, status);
        Changed?.Invoke(status);
    }

    private static string ReasonOf(TakeoverFailure failure) => failure switch
    {
        TakeoverFailure.LegacyAppRunning => DataReasons.LegacyAppRunning,
        TakeoverFailure.LegacyServiceRunning => DataReasons.LegacyServiceRunning,
        TakeoverFailure.OwnerUnknown => DataReasons.OwnerUnknown,
        TakeoverFailure.UnsupportedLayout => DataReasons.UnsupportedLayout,
        TakeoverFailure.BackupInvalid => DataReasons.BackupInvalid,
        TakeoverFailure.JournalCorrupt => DataReasons.JournalCorrupt,
        TakeoverFailure.Conflict => DataReasons.Conflict,
        _ => DataReasons.Internal,
    };

    public void Dispose()
    {
        Interlocked.Exchange(ref _database, null)?.Dispose();
        _gate.Dispose();
    }
}
