namespace Ncw.Agent.Environments;

internal static class ValueTaskExtensions
{
    /// <summary>丢掉结果,只保留完成和异常。</summary>
    public static ValueTask ToVoid<T>(this ValueTask<T> task) => task.IsCompletedSuccessfully ? ValueTask.CompletedTask : new ValueTask(task.AsTask());
}
