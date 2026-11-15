using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ncw.Client.Services;

namespace Ncw.Client.ViewModels;

public enum ImageLoadState
{
    /// <summary>还没开始(行还没滚进视野)。</summary>
    Idle,
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// 用户消息里的一张图片:加载状态和字节。加载是按需的(虚拟化列表把行实例化出来、视图挂上去才调 <see cref="EnsureLoadedAsync"/>),
/// 同一张图只加载一次,失败后 <see cref="RetryCommand"/> 再试。字节不进 Store(Store 只存会话里的引用);Client 不依赖 Avalonia,
/// 解码成位图是界面层的事。
/// </summary>
public sealed partial class ImageThumbViewModel(string mime, string dataRef, Func<CancellationToken, Task<ImageBytes?>> loader) : ObservableObject
{
    private Task? _flight;

    public string Mime { get; } = mime;

    /// <summary>引用(<c>data:</c> 或 <c>ncw://</c>):也是这张图的身份。</summary>
    public string DataRef { get; } = dataRef;

    [ObservableProperty] public partial ImageLoadState State { get; private set; }

    [ObservableProperty] public partial byte[]? Data { get; private set; }

    public bool IsLoading => State is ImageLoadState.Idle or ImageLoadState.Loading;

    public bool IsReady => State == ImageLoadState.Ready;

    public bool IsFailed => State == ImageLoadState.Failed;

    partial void OnStateChanged(ImageLoadState value)
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsFailed));
    }

    /// <summary>开始加载(已经加载过 / 正在加载就什么都不做,返回同一个任务)。</summary>
    public Task EnsureLoadedAsync()
    {
        if (State is ImageLoadState.Ready || _flight is { IsCompleted: false })
        {
            return _flight ?? Task.CompletedTask;
        }

        return _flight = LoadAsync();
    }

    [RelayCommand]
    private Task RetryAsync() => State == ImageLoadState.Failed ? (_flight = LoadAsync()) : Task.CompletedTask;

    private async Task LoadAsync()
    {
        State = ImageLoadState.Loading;
        try
        {
            var image = await loader(CancellationToken.None).ConfigureAwait(true);
            if (image is { Data.Length: > 0 })
            {
                Data = image.Data;
                State = ImageLoadState.Ready;
            }
            else
            {
                State = ImageLoadState.Failed;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            State = ImageLoadState.Failed;
        }
    }
}
