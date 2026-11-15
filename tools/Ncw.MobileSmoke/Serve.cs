namespace Ncw.MobileSmoke;

/// <summary>交互式:手动在模拟器上走流程时用。stdin 命令:<c>qr</c> 新二维码、<c>run</c> 发起一轮会触发审批的 Run、<c>state</c> 看信任列表 / 待审批 / 标记文件、<c>quit</c>。</summary>
internal static class Serve
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        await using var rig = new Rig();
        await rig.StartAsync(ct);
        Rig.Log("QR " + await rig.CreateQrAsync(ct));
        Rig.Log("commands: qr | qrpng <path> | run | state | quit");
        while (!ct.IsCancellationRequested)
        {
            var line = await Task.Run(Console.ReadLine, ct);
            if (line is null || line.Trim() == "quit")
            {
                break;
            }

            switch (line.Trim())
            {
                case "qr":
                    Rig.Log("QR " + await rig.CreateQrAsync(ct));
                    break;
                case "run":
                    await rig.StartRunAsync(ct);
                    break;
                case var cmd when cmd.StartsWith("qrpng ", StringComparison.Ordinal):
                    var qr = await rig.CreateQrAsync(ct);
                    QrPng.WriteWide(cmd[6..].Trim(), qr);
                    Rig.Log("QRPNG " + cmd[6..].Trim() + " " + qr);
                    break;
                case var cmd when cmd.StartsWith("calpng ", StringComparison.Ordinal):
                    QrPng.WriteCalibration(cmd[7..].Trim());
                    Rig.Log("CALPNG " + cmd[7..].Trim());
                    break;
                case "state":
                    var pending = await rig.PendingAsync(ct);
                    Rig.Log($"controllers=[{string.Join(", ", rig.Controllers.Select(c => $"{c.Name}/{c.Role}/connected={c.Connected}"))}] pending={pending.Length} marker={rig.MarkerWritten} upstreamRequests={rig.Upstream.ChatRequests}");
                    break;
            }
        }

        return 0;
    }
}
