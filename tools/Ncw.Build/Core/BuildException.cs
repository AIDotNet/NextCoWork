namespace Ncw.Build.Core;

/// <summary>构建步骤失败:消息直接给开发者看,进程以非零码退出。</summary>
internal sealed class BuildException(string message, Exception? inner = null) : Exception(message, inner);
