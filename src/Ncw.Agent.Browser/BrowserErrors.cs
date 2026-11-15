using System.Text.Json;

namespace Ncw.Agent.Browser;

/// <summary>结构化错误 key(进 Details 的 <c>errorKey</c>,控制端据此翻译;模型看到的是英文说明)。</summary>
public static class BrowserErrorKeys
{
    /// <summary>设备上没有找到 Chrome / Edge / Chromium(不会自动下载)。</summary>
    public const string NotInstalled = "browser.error.notInstalled";

    /// <summary>显式指定的可执行文件(设置或环境变量)不存在。</summary>
    public const string ExecutableMissing = "browser.error.executableMissing";

    /// <summary>项目的执行环境不是本机(SSH 环境暂不支持浏览器)。</summary>
    public const string RemoteEnvironment = "browser.error.remoteEnvironment";

    public const string LaunchFailed = "browser.error.launchFailed";

    public const string UrlRefused = "browser.error.urlRefused";

    public const string TabLimit = "browser.error.tabLimit";

    public const string TabNotFound = "browser.error.tabNotFound";

    public const string TabNotOwned = "browser.error.tabNotOwned";

    public const string NavigationFailed = "browser.error.navigationFailed";

    public const string BrowserGone = "browser.error.browserGone";

    public const string StaleObservation = "browser.error.staleObservation";
}

/// <summary>浏览器工具的可预期失败:<see cref="Key"/> 稳定,<see cref="Exception.Message"/> 是给模型看的英文。</summary>
public sealed class BrowserException(string key, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Key { get; } = key;
}
