using Ncw.MobileSmoke;

// Android 模拟器冒烟的桌面一侧(开发 / CI 工具,默认不跑):
//   NCW_ANDROID_SMOKE=1 dotnet run --project tools/Ncw.MobileSmoke -- serve   拉起隔离的 Agent + 假上游,打印二维码,按 stdin 命令操作(qr / run / state / quit)
//   NCW_ANDROID_SMOKE=1 dotnet run --project tools/Ncw.MobileSmoke -- smoke   完整的脚本化冒烟(adb 装机、深链配对、审批、长按允许),见 docs/design/11 §7
if (Environment.GetEnvironmentVariable("NCW_ANDROID_SMOKE") != "1")
{
    Console.WriteLine("SMOKE skipped: set NCW_ANDROID_SMOKE=1 to run the Android emulator smoke (it starts an isolated ncw-agent and talks to an adb device).");
    return 0;
}

var mode = args.FirstOrDefault() ?? "serve";
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(20));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

return mode switch
{
    "serve" => await Serve.RunAsync(cts.Token),
    "smoke" => await Smoke.RunAsync(args.Skip(1).ToArray(), cts.Token),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: Ncw.MobileSmoke serve|smoke");
    return 2;
}
