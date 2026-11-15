namespace Ncw.Agent;

/// <summary>
/// 同一个数据根只允许一个 ncw-agent(docs/design/04 §3):独占打开 <c>&lt;数据根&gt;/run/agent.lock</c>。
/// 内核在进程退出(包括崩溃、被杀)时释放锁,不会留下过期的锁;锁文件本身不删,避免「删除与重新创建」之间的竞争。
/// 没有这把锁,第二个 Agent 启动时会删掉第一个的 socket 文件并抢占端点(<c>LocalListener</c> 为了清理崩溃残留这样做)。
/// </summary>
internal sealed class AgentInstanceLock : IDisposable
{
    /// <summary>已有另一个实例在运行时的退出码(EX_TEMPFAIL);桌面端据此提示「数据目录正被另一个 Agent 使用」。</summary>
    public const int AlreadyRunningExitCode = 75;

    private readonly FileStream _stream;

    private AgentInstanceLock(FileStream stream) => _stream = stream;

    public static string PathFor(string dataRoot) => Path.Combine(dataRoot, "run", "agent.lock");

    /// <summary>拿到锁返回实例;已被占用返回 null。</summary>
    public static AgentInstanceLock? TryAcquire(string dataRoot)
    {
        var path = PathFor(dataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        try
        {
            // 写上 pid,排错时能知道谁占着(Windows 上别的进程读不了独占打开的文件,Unix 上可以)。
            stream.SetLength(0);
            var text = System.Text.Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            stream.Write(text);
            stream.Flush();
        }
        catch (IOException)
        {
        }

        return new AgentInstanceLock(stream);
    }

    public void Dispose() => _stream.Dispose();
}
