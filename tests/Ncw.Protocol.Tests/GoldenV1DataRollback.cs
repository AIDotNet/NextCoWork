namespace Ncw.Protocol.Tests;

/// <summary>
/// 设置 · 数据 · 回退到 2.x 的 DTO 的冻结向量(协议 v1,2.9 / 3.0 发布前批次生成)。旧向量不动:这一批只新增方法 / 载荷类型。
/// 测试失败说明某个 DTO 的键名 / 形状变了,那必须升协议版本或加能力开关,不能直接改向量。
/// </summary>
internal static class GoldenV1DataRollback
{
    public const string InfoAvailable =
        "83a9417661696c61626c65c3ab54616b656e4f7665724174cf0000018bcfe56800a750656e64696e67c2";
    public const string InfoNone =
        "83a9417661696c61626c65c2ab54616b656e4f7665724174c0a750656e64696e67c2";
    public const string RequestParams =
        "81b341636b6e6f776c65646765446174614c6f7373c3";

    public static string Of(string name) => name switch
    {
        "data.rollbackInfo.available" => InfoAvailable,
        "data.rollbackInfo.none" => InfoNone,
        "data.requestRollback.params" => RequestParams,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
