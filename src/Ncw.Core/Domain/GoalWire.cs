namespace Ncw.Core.Domain;

/// <summary>
/// 目标枚举的线上写法(小写驼峰字符串)。Agent(写出)和控制端(读入)共用这一张表,各写一份就会各漂各的。
/// 认不出的值读成 <c>null</c>(控制端按「未知」处理,不报错)。
/// </summary>
public static class GoalWire
{
    public static string ToWire(GoalOrigin origin) => origin switch
    {
        GoalOrigin.User => "user",
        GoalOrigin.ProposalDirect => "proposalDirect",
        GoalOrigin.ProposalApproved => "proposalApproved",
        _ => "restored",
    };

    public static GoalOrigin? ParseOrigin(string? wire) => wire switch
    {
        "user" => GoalOrigin.User,
        "proposalDirect" => GoalOrigin.ProposalDirect,
        "proposalApproved" => GoalOrigin.ProposalApproved,
        "restored" => GoalOrigin.Restored,
        _ => null,
    };

    public static string ToWire(GoalClearReason reason) => reason switch
    {
        GoalClearReason.UserClear => "userClear",
        GoalClearReason.Superseded => "superseded",
        GoalClearReason.Met => "met",
        GoalClearReason.Impossible => "impossible",
        GoalClearReason.SessionEnded => "sessionEnded",
        _ => "runFailed",
    };

    public static GoalClearReason? ParseClearReason(string? wire) => wire switch
    {
        "userClear" => GoalClearReason.UserClear,
        "superseded" => GoalClearReason.Superseded,
        "met" => GoalClearReason.Met,
        "impossible" => GoalClearReason.Impossible,
        "sessionEnded" => GoalClearReason.SessionEnded,
        "runFailed" => GoalClearReason.RunFailed,
        _ => null,
    };
}
