namespace Ncw.Protocol.Tests;

/// <summary>
/// 推送令牌登记 DTO 的冻结向量(协议 v1,M5 移动端批次生成)。旧向量不动:这一批只新增方法 / 载荷类型,没改帧格式也没改任何已冻结类型的键。
/// 测试失败说明某个 DTO 的键名 / 形状变了,那必须升协议版本或加能力开关,不能直接改向量。
/// </summary>
internal static class GoldenV1Push
{
    public const string RegisterParams =
        "83a8506c6174666f726da461706e73a5546f6b656ea86131623263336434aa41707056657273696f6ea5332e302e30";
    public const string RegisterParamsWithoutVersion =
        "83a8506c6174666f726da366636da5546f6b656ea966636d2d746f6b656eaa41707056657273696f6ec0";
    public const string RegisterResult =
        "82a8506c6174666f726da461706e73ac526567697374657265644174cf0000018bcfe56800";

    public static string Of(string name) => name switch
    {
        "push.register.params" => RegisterParams,
        "push.register.paramsNoVersion" => RegisterParamsWithoutVersion,
        "push.register.result" => RegisterResult,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };
}
