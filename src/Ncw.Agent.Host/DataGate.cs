using Ncw.Agent.Storage;
using Ncw.Agent.Storage.Credentials;
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
/// <item>回退到 2.x(04 §5,<see cref="RollbackToLegacy"/>):请求在关库之后执行(<see cref="Dispose"/>),上次没执行到的在下次启动、开库之前补上。
///   回退之后停在 <see cref="DataStates.RolledBack"/>,不自动再接管;<c>data.retry</c> 在这个状态下就是「重新迁移」。</item>
/// </list>
/// </summary>
public sealed class DataGate(
    string dataRoot,
    string deviceId,
    TimeProvider clock,
    TakeoverOptions? takeoverOptions = null,
    Func<ILegacySecretDecryptor>? legacySecrets = null) : IDisposable
{
    /// <summary>2.x 可能用过的 App 名:发行版的 productName、开发时的 package name(决定钥匙串条目名)。</summary>
    private static readonly string[] LegacyAppNames = ["NextCoWork", "nextcowork"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TakeoverOptions _takeover = takeoverOptions ?? new TakeoverOptions(clock);
    private DataStatus _status = new(DataStates.Preparing, null, null, null);
    private AgentDatabase? _database;

    /// <summary>用户在「已回退到 2.x」的状态下明确要求重新迁移(<see cref="RetryAsync"/>)。</summary>
    private volatile bool _retakeOver;

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

        if (RollbackToLegacy.Inspect(dataRoot).Pending)
        {
            return false; // 上次的回退请求没执行完:在 PrepareAsync 里补上
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

    /// <summary>
    /// <c>data.retry</c>:重试被挡住或失败的准备;在 <see cref="DataStates.RolledBack"/> 状态下,这是用户明确要求「重新迁移」。
    /// </summary>
    public Task<DataStatus> RetryAsync(CancellationToken ct = default)
    {
        if (Status.State == DataStates.RolledBack)
        {
            _retakeOver = true;
        }

        return PrepareAsync(ct);
    }

    /// <summary>设置 · 数据:能不能回退到 2.x、有没有已经请求的回退。</summary>
    public RollbackAvailability RollbackInfo() => RollbackToLegacy.Inspect(dataRoot);

    /// <summary>登记回退请求(在 <see cref="Dispose"/> 关库之后执行)。不能回退时抛 <see cref="TakeoverException"/>。</summary>
    public RollbackAvailability RequestRollback()
    {
        RollbackToLegacy.Request(dataRoot, clock.GetUtcNow().ToUnixTimeMilliseconds());
        return RollbackInfo();
    }

    public RollbackAvailability CancelRollback()
    {
        RollbackToLegacy.Cancel(dataRoot);
        return RollbackInfo();
    }

    private string Prepare()
    {
        try
        {
            LiveOwnerCheck.AssertNone(LiveOwnerCheck.RootsFor(dataRoot), _takeover.IsAlive);
            // 上次请求的回退没在关库时执行到(Agent 被强杀):库还没打开,现在补上
            if (RollbackToLegacy.RunPending(dataRoot, _takeover) is not null)
            {
                Console.Error.WriteLine("ncw-agent: the data root was rolled back to NextCoWork 2.x");
            }

            var kind = LegacyLayout.Inspect(dataRoot);
            if (kind == DataRootKind.Legacy && RollbackToLegacy.IsRolledBack(dataRoot) && !_retakeOver)
            {
                // 用户回到了 2.x:不自动再接管,等用户在界面上明确点「重新迁移」
                Set(new DataStatus(DataStates.RolledBack, null, null, null));
                return Status.State;
            }

            switch (kind)
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

        var failedCredentials = MigrateLegacyCredentials(database);
        var projects = new WorkspaceRepository(database, clock).List().Count;
        var sessions = database.SessionCount();
        BecomeReady(database, new TakeoverSummary(projects, sessions, failedCredentials));
    }

    /// <summary>
    /// 04 §4 第 4 步:把 2.x 的旧 safeStorage 凭据改存成 NCK1。只在接管时做(macOS 上会弹钥匙串授权框,接管页先说明);
    /// 解不开的行原样保留,读取时仍会尝试旧格式,并计入「需要重新输入」。
    /// </summary>
    private int MigrateLegacyCredentials(AgentDatabase database)
    {
        var decryptor = legacySecrets?.Invoke()
            ?? ChromiumOsCrypt.ForCurrentPlatform(LiveOwnerCheck.RootsFor(dataRoot)[^1], LegacyAppNames);
        var store = new CredentialStore(database, dataRoot, decryptor);
        if (store.LegacyCount() == 0)
        {
            return 0;
        }

        Set(new DataStatus(DataStates.TakingOver, DataSteps.Credentials, null, null));
        return store.MigrateLegacy().Failed;
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

    /// <summary>关库;有回退请求就在关库之后执行(Agent 优雅退出的最后一步,这时会话、Run 都已经收尾)。</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _database, null)?.Dispose();
        try
        {
            if (RollbackToLegacy.RunPending(dataRoot, _takeover) is not null)
            {
                Console.Error.WriteLine("ncw-agent: the data root was rolled back to NextCoWork 2.x");
            }
        }
        catch (Exception e) when (e is TakeoverException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            // 退出路径上不能抛;能重试的请求还留着,下次启动开库之前再做
            Console.Error.WriteLine($"ncw-agent: rolling back to 2.x failed: {e.Message}");
        }

        _gate.Dispose();
    }
}
