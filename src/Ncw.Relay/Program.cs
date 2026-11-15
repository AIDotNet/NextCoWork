using Ncw.Relay;

// 服务器中继的入口(部署物,不是用户命令行):配置全部来自环境变量(见 RelayConfig),没有命令行参数。
var options = RelayConfig.FromEnvironment(Environment.GetEnvironmentVariable);
await using var relay = await RelayServer.StartAsync(options).ConfigureAwait(false);
// 不 dispose:ProcessExit 在主流程结束(含 using 的释放)之后才触发,那时再 Cancel 一个已释放的 CTS 会抛 ObjectDisposedException 把进程以 134 退出。
var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
// SIGTERM(容器 / systemd 的停止信号)由 ASP.NET 的 ConsoleLifetime 接管(取消默认的进程退出),只会触发 ApplicationStopping:
// 主流程不跟着退出的话进程会一直活着,直到被 SIGKILL。所以宿主开始停止时也要结束主流程。
relay.Stopping.Register(stop.Cancel);

// 构建工具的产物自检用(`tools/Ncw.Build` 的 build-relay):NCW_RELAY_LIFELINE=stdin 时,stdin 关闭(EOF)就优雅退出。
// Windows 上没有能发给后台进程的 SIGTERM 等价物,靠它才能验证「优雅退出」;正常部署不设置,不读 stdin。
switch (Environment.GetEnvironmentVariable("NCW_RELAY_LIFELINE"))
{
    case null or "":
        break;
    case "stdin":
        _ = Task.Run(() =>
        {
            try
            {
                Console.In.ReadToEnd();
            }
            catch (IOException)
            {
            }

            stop.Cancel();
        });
        break;
    default:
        throw new FormatException("NCW_RELAY_LIFELINE must be 'stdin' or unset");
}

try
{
    await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
}
