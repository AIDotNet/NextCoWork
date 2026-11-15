namespace Ncw.Build.Core;

internal interface IBuildLog
{
    void Step(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message);
}

internal sealed class ConsoleBuildLog : IBuildLog
{
    public void Step(string message) => Console.WriteLine($"==> {message}");

    public void Info(string message) => Console.WriteLine($"    {message}");

    public void Warn(string message) => Console.WriteLine($"    WARN  {message}");

    public void Error(string message) => Console.Error.WriteLine($"ERROR {message}");
}

/// <summary>测试用:把所有输出收集起来。</summary>
internal sealed class MemoryBuildLog : IBuildLog
{
    public List<string> Lines { get; } = [];

    public void Step(string message) => Lines.Add("STEP " + message);

    public void Info(string message) => Lines.Add("INFO " + message);

    public void Warn(string message) => Lines.Add("WARN " + message);

    public void Error(string message) => Lines.Add("ERROR " + message);
}
