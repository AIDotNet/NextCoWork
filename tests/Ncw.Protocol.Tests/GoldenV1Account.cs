namespace Ncw.Protocol.Tests;

/// <summary>
/// 账户委托的冻结向量(协议 v1,M4 账户域)。旧向量不动:这一批只新增方法和载荷类型,没改帧格式也没改任何已冻结类型的键,所以仍是协议 v1。
/// 这些测试失败说明某个 DTO 的键名 / 形状变了,必须升协议版本或加能力开关,不能直接改向量。
/// </summary>
internal static class GoldenV1Account
{
    public const string AttachParams =
        "84a5546f6b656ea5746f6b2d31a9457870697265734174cf000001a3185c5000a94163636f756e744964a27531a65465616d4964a27431";
    public const string StatusAttached =
        "84a84174746163686564c3a94163636f756e744964a27531a65465616d4964a27431a9457870697265734174cf000001a3185c5000";
    public const string StatusDetached =
        "84a84174746163686564c2a94163636f756e744964c0a65465616d4964c0a9457870697265734174c0";
}
