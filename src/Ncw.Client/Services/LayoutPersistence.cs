using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ncw.Client.Stores;
using Ncw.Core.Domain;

namespace Ncw.Client.Services;

/// <summary>项目外观的读写位置。桌面 / 手机都是客户端目录里的一个 JSON 文件;测试里换成内存实现。</summary>
public interface ILayoutRepository
{
    ImmutableDictionary<ProjectRef, ProjectAppearance>? Load();

    void Save(ImmutableDictionary<ProjectRef, ProjectAppearance> appearance);
}

/// <summary>
/// 项目外观(自定义图标、颜色)的本地持久化。账户配置同步(<see cref="Accounts.IConfigSync"/>)在后端到位前是关闭的,
/// 没有这一层,用户改的图标重启就丢了。启动时把文件里的外观合进 <see cref="ClientStores.Layout"/>(文件里的压过内存里的),
/// 之后只在 <see cref="LayoutState.Appearance"/> 变了时(不是每次拖动项目栏)合并写回。项目栏的顺序和分组不在这里(仍由设备的项目列表重建)。
/// </summary>
public sealed class LayoutPersistence : IDisposable
{
    private readonly ClientStores _stores;
    private readonly ILayoutRepository _repository;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private ImmutableDictionary<ProjectRef, ProjectAppearance> _last;
    private ImmutableDictionary<ProjectRef, ProjectAppearance>? _pending;

    public LayoutPersistence(ClientStores stores, ILayoutRepository repository, TimeSpan? delay = null)
    {
        _stores = stores;
        _repository = repository;
        Delay = delay ?? TimeSpan.FromMilliseconds(400);
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);

        if (SafeLoad() is { Count: > 0 } saved)
        {
            stores.Layout.Update(s => s with { Appearance = s.Appearance.SetItems(saved) });
        }

        _last = stores.Layout.State.Appearance;
        stores.Layout.Changed += OnChanged;
    }

    public TimeSpan Delay { get; }

    /// <summary>立刻写出还没写的外观(退出时调用)。</summary>
    public void Flush()
    {
        ImmutableDictionary<ProjectRef, ProjectAppearance>? appearance;
        lock (_gate)
        {
            appearance = _pending;
            _pending = null;
        }

        if (appearance is not null)
        {
            try
            {
                _repository.Save(appearance);
            }
            catch (Exception)
            {
                // 写不进去不影响使用;下次变化时再试。
            }
        }
    }

    public void Dispose()
    {
        _stores.Layout.Changed -= OnChanged;
        Flush();
        _timer.Dispose();
    }

    private void OnChanged()
    {
        var appearance = _stores.Layout.State.Appearance;
        if (ReferenceEquals(appearance, _last))
        {
            return;
        }

        _last = appearance;
        lock (_gate)
        {
            _pending = appearance;
        }

        _timer.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    private ImmutableDictionary<ProjectRef, ProjectAppearance>? SafeLoad()
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

/// <summary>把项目外观存成一个 JSON 文件(<c>layout.json</c>):先写临时文件再改名。</summary>
public sealed class JsonFileLayoutRepository(string path) : ILayoutRepository
{
    public string Path { get; } = path;

    public ImmutableDictionary<ProjectRef, ProjectAppearance>? Load()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        using var stream = File.OpenRead(Path);
        return JsonSerializer.Deserialize(stream, LayoutJsonContext.Default.LayoutFileDto)?.ToAppearance();
    }

    public void Save(ImmutableDictionary<ProjectRef, ProjectAppearance> appearance)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        using (var stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, LayoutFileDto.From(appearance), LayoutJsonContext.Default.LayoutFileDto);
        }

        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>文件里的形状。字段名固定,和 <see cref="ProjectAppearance"/> 解耦;新增字段只追加成可空项,版本号不动。</summary>
public sealed record LayoutFileDto(int Version, ProjectAppearanceDto[]? Appearance)
{
    public static LayoutFileDto From(ImmutableDictionary<ProjectRef, ProjectAppearance> appearance) => new(
        1,
        [.. appearance.OrderBy(kv => kv.Key.DeviceId, StringComparer.Ordinal).ThenBy(kv => kv.Key.WorkspaceId, StringComparer.Ordinal)
            .Select(kv => new ProjectAppearanceDto(kv.Key.DeviceId, kv.Key.WorkspaceId, kv.Value.Glyph, kv.Value.Color, kv.Value.Image))]);

    /// <summary>读进来的值可能被手改过:缺 id / 颜色的条目丢掉,过长的图片当作没有。</summary>
    public ImmutableDictionary<ProjectRef, ProjectAppearance> ToAppearance()
    {
        var builder = ImmutableDictionary.CreateBuilder<ProjectRef, ProjectAppearance>();
        foreach (var a in Appearance ?? [])
        {
            if (a is null || string.IsNullOrEmpty(a.DeviceId) || string.IsNullOrEmpty(a.WorkspaceId) || string.IsNullOrEmpty(a.Color))
            {
                continue;
            }

            var image = a.Image is { Length: > 0 and <= ProjectIconSpec.MaxStoredLength } ? a.Image : null;
            builder[new ProjectRef(a.DeviceId, a.WorkspaceId)] = new ProjectAppearance(string.IsNullOrEmpty(a.Glyph) ? null : a.Glyph, a.Color, image);
        }

        return builder.ToImmutable();
    }
}

public sealed record ProjectAppearanceDto(string DeviceId, string WorkspaceId, string? Glyph, string Color, string? Image = null);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(LayoutFileDto))]
internal sealed partial class LayoutJsonContext : JsonSerializerContext;
