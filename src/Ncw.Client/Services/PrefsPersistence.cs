using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Client.Shortcuts;
using Ncw.Client.Stores;
using Ncw.Client.ViewModels;

namespace Ncw.Client.Services;

/// <summary>界面偏好的读写位置。桌面端是一个 JSON 文件;测试里换成内存实现。</summary>
public interface IPrefsRepository
{
    UiPrefs? Load();

    void Save(UiPrefs prefs);
}

/// <summary>
/// 启动时把偏好读进 <see cref="ClientStores.Prefs"/>,之后 Store 每次变化在短暂合并后写回。
/// 拖分隔条会连续改宽度,合并后只写最后一次。
/// </summary>
public sealed class PrefsPersistence : IDisposable
{
    private readonly ClientStores _stores;
    private readonly IPrefsRepository _repository;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private UiPrefs? _pending;

    public PrefsPersistence(ClientStores stores, IPrefsRepository repository, TimeSpan? delay = null)
    {
        _stores = stores;
        _repository = repository;
        Delay = delay ?? TimeSpan.FromMilliseconds(400);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        if (SafeLoad() is { } saved)
        {
            stores.Prefs.Update(_ => saved.Normalize());
        }

        stores.Prefs.Changed += OnChanged;
    }

    public TimeSpan Delay { get; }

    /// <summary>立刻写出还没写的偏好(退出时调用)。</summary>
    public void Flush()
    {
        UiPrefs? prefs;
        lock (_gate)
        {
            prefs = _pending;
            _pending = null;
        }

        if (prefs is not null)
        {
            try
            {
                _repository.Save(prefs);
            }
            catch (Exception)
            {
                // 偏好写不进去不应该影响使用;下次变化时会再试。
            }
        }
    }

    public void Dispose()
    {
        _stores.Prefs.Changed -= OnChanged;
        Flush();
        _timer.Dispose();
    }

    private void OnChanged()
    {
        lock (_gate)
        {
            _pending = _stores.Prefs.State;
        }

        _timer.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    private UiPrefs? SafeLoad()
    {
        try
        {
            return _repository.Load();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>把偏好存成一个 JSON 文件:先写临时文件再改名,写到一半崩溃也不会留下坏文件。</summary>
public sealed class JsonFilePrefsRepository(string path) : IPrefsRepository
{
    public string Path { get; } = path;

    public UiPrefs? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        using var stream = File.OpenRead(Path);
        return JsonSerializer.Deserialize(stream, PrefsJsonContext.Default.UiPrefsDto)?.ToPrefs();
    }

    public void Save(UiPrefs prefs)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, UiPrefsDto.From(prefs), PrefsJsonContext.Default.UiPrefsDto);
        }

        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>文件里的形状。字段名固定,和 <see cref="UiPrefs"/> 解耦,以后改记录不会破坏旧文件;新增字段只追加成可空项(<c>Motion</c> 缺失 = 跟随系统;
/// <c>Shortcuts</c> 缺失 = 全用默认,键是 <see cref="AppCommand"/> 的成员名,不认识的键丢掉;<c>Attention</c> 缺失 = 通知全开),版本号不动。</summary>
public sealed record UiPrefsDto(
    int Version,
    double SidebarWidth,
    double InspectorWidth,
    bool IsSidebarOpen,
    bool IsInspectorOpen,
    string[]? InspectorTabs,
    string? Motion = null,
    Dictionary<string, string>? Shortcuts = null,
    AttentionPrefsDto? Attention = null,
    string? Theme = null)
{
    public static UiPrefsDto From(UiPrefs prefs) => new(
        1, prefs.SidebarWidth, prefs.InspectorWidth, prefs.IsSidebarOpen, prefs.IsInspectorOpen,
        [.. prefs.InspectorTabs.Select(t => t.ToString())], prefs.Motion.ToString(),
        prefs.ShortcutOverrides.Count == 0 ? null : prefs.ShortcutOverrides.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value),
        prefs.Attention is { } a ? new AttentionPrefsDto(a.NotifyPending, a.NotifyCompleted, a.NotifyFailed, a.Badge, a.Tray) : null,
        prefs.Theme.ToString());

    public UiPrefs ToPrefs() => new(
        SidebarWidth, InspectorWidth, IsSidebarOpen, IsInspectorOpen,
        [.. (InspectorTabs ?? []).Select(t => Enum.TryParse<InspectorTabKind>(t, out var kind) ? kind : (InspectorTabKind?)null)
            .Where(k => k is not null).Select(k => k!.Value)],
        Enum.TryParse<UiMotionPreference>(Motion, out var motion) && Enum.IsDefined(motion) ? motion : UiMotionPreference.System,
        Shortcuts is null ? null : Shortcuts
            .Select(kv => (Ok: Enum.TryParse<AppCommand>(kv.Key, out var command) && Enum.IsDefined(command), Command: command, kv.Value))
            .Where(x => x.Ok && x.Value is not null)
            .ToImmutableDictionary(x => x.Command, x => x.Value),
        Attention is { } a ? new AttentionPrefs(a.NotifyPending ?? true, a.NotifyCompleted ?? true, a.NotifyFailed ?? true, a.Badge ?? true, a.Tray ?? true) : null,
        Enum.TryParse<UiThemePreference>(Theme, out var theme) && Enum.IsDefined(theme) ? theme : UiThemePreference.Dark);
}

/// <summary>通知偏好在文件里的形状:每项可空(缺失 = 默认开)。</summary>
public sealed record AttentionPrefsDto(bool? NotifyPending, bool? NotifyCompleted, bool? NotifyFailed, bool? Badge, bool? Tray);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UiPrefsDto))]
internal sealed partial class PrefsJsonContext : JsonSerializerContext;
