using Ncw.Agent.Llm;
using Ncw.Agent.Llm.Routing;
using Ncw.Agent.Services.Attachments;
using Ncw.Core.Domain;

namespace Ncw.Agent.Host.Conversation;

/// <summary>出站模型请求从经过作用域校验的会话附件解析图片，转录只保留地址。</summary>
internal sealed class SessionImageResolver(AttachmentService attachments) : IImageResolver
{
    public ValueTask<string> ResolveAsync(string dataRef, string mime, RequestContext context, CancellationToken ct)
    {
        if (context.SessionId is not { Length: > 0 } sessionId
            || Attachments.ParseUrl(dataRef) is not { Scope: Attachments.Session } locator || locator.OwnerId != sessionId)
        {
            throw Unreadable();
        }

        try
        {
            using var buffer = new MemoryStream();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var part = attachments.Read(dataRef, buffer.Length, AttachmentService.MaxChunkBytes);
                if (part.Total > Attachments.MaxBytes || Attachments.NormalizeImageMime(mime) != part.Mime
                    || (!part.Eof && part.Data.Length == 0))
                {
                    break;
                }

                buffer.Write(part.Data);
                if (part.Eof)
                {
                    if (buffer.Length == 0)
                    {
                        break;
                    }

                    return ValueTask.FromResult($"data:{part.Mime};base64,{Convert.ToBase64String(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))}");
                }
            }
        }
        catch (AttachmentException)
        {
            // 不把设备文件位置泄漏到模型端。
        }
        catch (IOException)
        {
            // 附件可能在读的过程中被移除。
        }

        throw Unreadable();
    }

    private static ImageInputException Unreadable() => new(AgentError.Create(
        AgentErrorCode.Provider, "The session image attachment could not be read.", false, messageKey: LlmErrorKeys.ImageInput));
}
