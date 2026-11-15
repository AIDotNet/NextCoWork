using Ncw.Protocol.Methods;

namespace Ncw.Agent.Gateway;

/// <summary>
/// 网关暴露哪些方法(HTTP 映射的是「同一张方法表」,但不是全部):
/// <list type="bullet">
/// <item><b>承载</b>:网关调用者按 <see cref="Transports.Remote"/> 对待(它是网络来的),所以<b>只本机</b>的方法(远程开关、配对二维码、信任管理、恢复备份、危险清理、导入、网关的密钥与开关……)天然调不到,路由统一拒绝,不是网关自己判断;</item>
/// <item><b>角色</b>:<see cref="Role.Unpaired"/> 专用的配对方法(<c>pair.begin / finish / claim</c>)不暴露:配对是 TLS 通道上的事;</item>
/// <item><b>二进制参数</b>:JSON 里没法判断一个字符串该不该是 base64 二进制,带 <c>byte[]</c> 参数的方法(附件分块上传、导入分块)不暴露;</item>
/// <item><b>流式</b>:终端等二进制输出走协议通道;网关只做 query / command 和 JSON 主题的 SSE。</item>
/// </list>
/// </summary>
public static class GatewayExposure
{
    public static bool Exposes(MethodDef method) =>
        (method.Allowed & Transports.Remote) != 0
        && method.MinRole >= Role.Viewer
        && method.Name != "attachments.upload.chunk"
        && method.Name != "storage.import.write"
        && !method.Name.StartsWith("terminals.", StringComparison.Ordinal);
}
