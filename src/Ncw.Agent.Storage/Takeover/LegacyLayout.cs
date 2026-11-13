namespace Ncw.Agent.Storage.Takeover;

/// <summary>一份数据根在启动时的状态。</summary>
public enum DataRootKind
{
    /// <summary>没有数据库:全新安装。</summary>
    Fresh,

    /// <summary>数据库里有所有权标记:已经由 3.0 接管(或 3.0 新建)。</summary>
    Owned,

    /// <summary>有数据库但没有所有权标记:2.x 的数据,需要接管。</summary>
    Legacy,
}

/// <summary>2.x 的数据布局(<c>main</c> 分支 <c>src/main/index.ts</c>、<c>db/flat-layout.ts</c>)。</summary>
public static class LegacyLayout
{
    /// <summary>2.x 的 profile 根下放应用数据的子目录。</summary>
    public const string DataSubdirectory = "data";

    /// <summary>2.x 的 profile 根:<c>~/.next-cowork</c>(开发和发行版同一个)。</summary>
    public static string DefaultProfileRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".next-cowork");

    /// <summary>2.x 的数据根,也是 3.0 原地接管后使用的数据根。</summary>
    public static string DefaultDataRoot() => Path.Combine(DefaultProfileRoot(), DataSubdirectory);

    public static DataRootKind Inspect(string dataRoot)
    {
        var db = Path.Combine(dataRoot, AgentDatabase.FileName);
        if (!File.Exists(db))
        {
            return DataRootKind.Fresh;
        }

        return Ownership.Read(db) is null ? DataRootKind.Legacy : DataRootKind.Owned;
    }

    /// <summary>
    /// 数据根里没有库,但旧布局里有 2.x 的数据:扁平布局(库直接在 profile 根)或者 Electron 默认 userData
    /// (<c>~/Library/Application Support/NextCoWork</c> 等)。这两种由 2.x 自己在启动时整理成 <c>data/</c>;
    /// 3.0 不重复实现这段搬家逻辑,而是请用户先用 2.x 打开一次。只对默认数据根检查 Electron 默认目录。
    /// </summary>
    public static bool HasUnmigratedLayout(string dataRoot)
    {
        if (File.Exists(Path.Combine(dataRoot, AgentDatabase.FileName)))
        {
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (Path.GetFileName(full) == DataSubdirectory && Path.GetDirectoryName(full) is { } profile
            && File.Exists(Path.Combine(profile, AgentDatabase.FileName)))
        {
            return true;
        }

        return string.Equals(full, Path.GetFullPath(DefaultDataRoot()), StringComparison.Ordinal)
            && ElectronDefaultUserData() is { } electron
            && File.Exists(Path.Combine(electron, AgentDatabase.FileName));
    }

    private static string? ElectronDefaultUserData()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextCoWork");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "NextCoWork")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".config"), "NextCoWork");
    }
}
