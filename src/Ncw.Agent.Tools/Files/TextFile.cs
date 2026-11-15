using System.Text;
using Ncw.Agent.Environments;

namespace Ncw.Agent.Tools;

/// <summary>一个文本文件读进来的样子:磁盘上的原样(去掉 BOM)+ BOM / 换行风格,写回去时据此还原。</summary>
internal sealed record TextFile(string Text, bool HasBom, NewlineStyle? Newline, long Size)
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async ValueTask<TextFile> ReadAsync(IEnvironmentFs fs, EnvPath path, long maxBytes, CancellationToken ct)
    {
        var bytes = await fs.ReadBytesAsync(path, maxBytes, ct).ConfigureAwait(false);
        return Decode(bytes);
    }

    public static TextFile Decode(byte[] bytes)
    {
        var bom = TextNewlines.HasUtf8Bom(bytes);
        var text = Utf8.GetString(bom ? bytes.AsSpan(3) : bytes);
        return new TextFile(text, bom, TextNewlines.Detect(text), bytes.Length);
    }

    /// <summary>按文件原来的 BOM 和换行风格还原。<paramref name="lfText"/> 是已经归一成 LF 的内容。</summary>
    public byte[] Encode(string lfText, string environmentNewLine)
    {
        var style = Newline ?? (environmentNewLine == "\r\n" ? NewlineStyle.CrLf : NewlineStyle.Lf);
        return EncodeAs(TextNewlines.Apply(lfText, style), HasBom);
    }

    public static byte[] EncodeAs(string text, bool bom)
    {
        var body = Utf8.GetBytes(text);
        if (!bom)
        {
            return body;
        }

        var result = new byte[body.Length + 3];
        result[0] = 0xEF;
        result[1] = 0xBB;
        result[2] = 0xBF;
        body.CopyTo(result, 3);
        return result;
    }
}
