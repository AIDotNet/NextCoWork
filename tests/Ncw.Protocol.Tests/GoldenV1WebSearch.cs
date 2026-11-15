namespace Ncw.Protocol.Tests;

/// <summary>
/// 搜索提供方 DTO 的冻结向量(协议 v1,websearch 批次生成)。旧向量不动:这一批只新增方法 / 主题 / 载荷类型,没改帧格式也没改任何已冻结类型的键。
/// 这些测试失败说明某个 DTO 的键名 / 形状变了,那必须升协议版本或加能力开关,不能直接改向量(见 <see cref="WebSearchDtoTests"/>)。
/// </summary>
internal static class GoldenV1WebSearch
{
    public const string State =
        "82a756657273696f6e07a950726f7669646572739388a24964a6746176696c79a44e616d65a6544156494c59a64b657955726cbb68747470733a2f2f6b6579732e6578616d706c652f746176696c79ae556e617661696c61626c654b6579c0a7456e61626c6564c3a85072696f7269747900a64861734b6579c3a54c61737434a43132333488a24964a3657861a44e616d65a3455841a64b657955726cb868747470733a2f2f6b6579732e6578616d706c652f657861ae556e617661696c61626c654b6579c0a7456e61626c6564c2a85072696f7269747901a64861734b6579c2a54c61737434c088a24964a462696e67a44e616d65a442494e47a64b657955726cb968747470733a2f2f6b6579732e6578616d706c652f62696e67ae556e617661696c61626c654b6579ba7765627365617263682e756e617661696c61626c652e62696e67a7456e61626c6564c2a85072696f7269747902a64861734b6579c2a54c61737434c0";
    public const string SetEnabled =
        "82a24964a6746176696c79a7456e61626c6564c3";
    public const string Reorder =
        "81a349647393a3657861a6746176696c79a56272617665";
    public const string SetCredential =
        "82a24964a6746176696c79a64170694b6579af74766c792d30313233343536373839";
    public const string CredentialInfo =
        "82a64861734b6579c3a54c61737434a436373839";
    public const string Test =
        "84a24f6bc2a94c6174656e63794d73c0a34b6579b57765627365617263682e746573742e6661696c6564a644657461696cd925546176696c792072657475726e656420485454502034303120556e617574686f72697a6564";

    public static string Of(string name) => name switch
    {
        "websearch.state" => State,
        "websearch.setEnabled" => SetEnabled,
        "websearch.reorder" => Reorder,
        "websearch.setCredential" => SetCredential,
        "websearch.credentialInfo" => CredentialInfo,
        "websearch.test" => Test,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
