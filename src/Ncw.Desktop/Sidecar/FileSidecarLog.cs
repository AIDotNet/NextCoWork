using System.Globalization;

namespace Ncw.Desktop.Sidecar;

/// <summary>
/// 把 Agent 的输出追加到 <c>&lt;数据根&gt;/logs/agent.log</c>:超过上限就把旧的改名为 <c>agent.log.1</c>(覆盖更旧的),只留一份。
/// 写日志失败不能影响守护,所以全部吞掉 IO 错误。
/// </summary>
public sealed class FileSidecarLog(string path, long maxBytes = 2 * 1024 * 1024, TimeProvider? clock = null) : ISidecarLog
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length >= maxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) + " " + line + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
