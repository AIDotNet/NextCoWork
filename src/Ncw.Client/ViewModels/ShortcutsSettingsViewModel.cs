using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Shortcuts;
using Ncw.Client.Stores;

namespace Ncw.Client.ViewModels;

/// <summary>「快捷键」页的一行:一个意图、它现在的手势、是否被改过、是否正在录制。</summary>
public sealed partial class ShortcutRowViewModel(AppCommand command) : ObservableObject
{
    public AppCommand Command { get; } = command;

    [ObservableProperty] public partial string? Gesture { get; internal set; }

    [ObservableProperty] public partial bool IsCustom { get; internal set; }

    [ObservableProperty] public partial bool IsRecording { get; internal set; }

    public bool HasGesture => !string.IsNullOrEmpty(Gesture);

    partial void OnGestureChanged(string? value) => OnPropertyChanged(nameof(HasGesture));
}

/// <summary>
/// 设置 · 本应用 · 快捷键(docs/design/03 §11、07 §3):列出每个意图的生效手势,可以录制新的、移除、恢复默认。
/// 真源是 <see cref="UiPrefs.Shortcuts"/>(只存用户改过的);生效值由纯函数 <see cref="ShortcutCatalog.Resolve"/> 算出,这里不缓存。
/// 录制时的按键由视图捕获后交给 <see cref="Record"/>;录制期间全局快捷键不触发(视图负责)。
/// </summary>
public sealed partial class ShortcutsSettingsViewModel : ObservableObject
{
    private readonly ClientStores _stores;
    private readonly Func<bool> _isMac;

    public ShortcutsSettingsViewModel(ClientStores stores, Func<bool>? isMac = null)
    {
        _stores = stores;
        _isMac = isMac ?? OperatingSystem.IsMacOS;
        Rows = [.. Enum.GetValues<AppCommand>().Select(c => new ShortcutRowViewModel(c))];
        stores.Prefs.Changed += Refresh;
        Refresh();
    }

    public ObservableCollection<ShortcutRowViewModel> Rows { get; }

    /// <summary>正在录制的那一行;没有为 null。</summary>
    [ObservableProperty] public partial ShortcutRowViewModel? Recording { get; private set; }

    /// <summary>上一次录制把哪个意图的快捷键挤掉了(界面提示一句);没有为 null。</summary>
    [ObservableProperty] public partial AppCommand? Displaced { get; private set; }

    /// <summary>上一次录到的组合不能用(只有 Shift 或没有修饰键)。</summary>
    [ObservableProperty] public partial bool LastRejected { get; private set; }

    public bool HasCustom => Rows.Any(r => r.IsCustom);

    [RelayCommand]
    private void StartRecording(ShortcutRowViewModel row)
    {
        Cancel();
        Displaced = null;
        LastRejected = false;
        Recording = row;
        row.IsRecording = true;
    }

    /// <summary>录制中按了 Esc / 点了别处。</summary>
    [RelayCommand]
    public void Cancel()
    {
        if (Recording is { } row)
        {
            row.IsRecording = false;
        }

        Recording = null;
    }

    /// <summary>视图录到了一个组合(Avalonia 手势文本)。不能用的组合不保存,留在录制状态等下一次按键。</summary>
    public bool Record(string gesture)
    {
        if (Recording is not { } row)
        {
            return false;
        }

        if (!ShortcutCatalog.IsUsable(gesture))
        {
            LastRejected = true;
            return false;
        }

        var mac = _isMac();
        var before = ShortcutCatalog.Resolve(_stores.Prefs.State.ShortcutOverrides, mac);
        Displaced = ShortcutCatalog.ConflictOf(before, row.Command, gesture);
        LastRejected = false;
        Update(row.Command, gesture);
        Cancel();
        return true;
    }

    [RelayCommand]
    private void Clear(ShortcutRowViewModel row)
    {
        Cancel();
        Displaced = null;
        Update(row.Command, "");
    }

    [RelayCommand]
    private void Reset(ShortcutRowViewModel row)
    {
        Cancel();
        Displaced = null;
        Update(row.Command, null);
    }

    [RelayCommand]
    private void ResetAll()
    {
        Cancel();
        Displaced = null;
        _stores.Prefs.Update(p => p with { Shortcuts = null });
    }

    private void Update(AppCommand command, string? gesture) =>
        _stores.Prefs.Update(p => p with { Shortcuts = ShortcutCatalog.Assign(p.ShortcutOverrides, command, gesture, _isMac()) });

    private void Refresh()
    {
        var bindings = ShortcutCatalog.Resolve(_stores.Prefs.State.ShortcutOverrides, _isMac());
        foreach (var binding in bindings)
        {
            var row = Rows[(int)binding.Command];
            row.Gesture = binding.Gesture;
            row.IsCustom = binding.IsCustom;
        }

        OnPropertyChanged(nameof(HasCustom));
    }
}
