namespace Ncw.Client.Services;

/// <summary>
/// 打开系统浏览器(OAuth 授权页、文档链接)。授权页由<b>控制端</b>打开,不是设备:设备可能在另一台电脑上。
/// UI 层用 Avalonia 的 Launcher 实现;测试里换成记录调用的假实现。
/// </summary>
public interface IUrlLauncher
{
    Task OpenAsync(string url);
}

/// <summary>没有界面时的默认实现:什么也不做。</summary>
public sealed class NoUrlLauncher : IUrlLauncher
{
    public static readonly NoUrlLauncher Instance = new();

    public Task OpenAsync(string url) => Task.CompletedTask;
}
