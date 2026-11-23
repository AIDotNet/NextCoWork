namespace Ncw.Protocol.Methods;

// M4 杂项 Agent 服务的能力名(docs/design/09 §8.4「设置 / 用量 / 定时 / 存储 / 搜索 / 附件 / 输入框 / 小组件 / 网关」)。
// 设备在 welcome.features 里列出自己已注册方法所属的能力;控制端据此决定能调哪些方法、订阅哪些主题,老设备缺哪个就降级。
public static partial class Capabilities
{
    /// <summary>设备 / 项目设置:<c>settings.*</c> 和 <c>settings</c> 主题(读取 member 起,写入 owner)。</summary>
    public const string Settings = "settings";

    /// <summary>设备支持配置图片、视频和音频生成工具,含设置中的可选媒体模型字段。</summary>
    public const string SettingsMedia = "settings.media";

    /// <summary>用量统计:<c>usage.*</c>(只读,viewer 起)。</summary>
    public const string Usage = "usage";

    /// <summary>定时任务:<c>schedules.*</c> 和 <c>schedules</c> 主题(读取 viewer 起,写入 member 起)。</summary>
    public const string Schedules = "schedules";

    /// <summary>存储维护:<c>storage.*</c>(统计 viewer 起;整理 / 备份 / 清理 / 导入导出 owner,危险操作仅本机通道)。</summary>
    public const string Storage = "storage";

    /// <summary>全局会话搜索:<c>search.*</c>(viewer 起)。</summary>
    public const string Search = "search";

    /// <summary>附件:<c>attachments.*</c>(分块上传 / 移除 / 按会话列出 / 项目上传三步)。</summary>
    public const string Attachments = "attachments";

    /// <summary>输入框:<c>composer.*</c>(建议下一步、历史、补全、取消)。</summary>
    public const string Composer = "composer";

    /// <summary>内联可视化小组件:<c>widgets.*</c>(状态、提交、动作)。</summary>
    public const string Widgets = "widgets";

    /// <summary>开放网关:<c>gateway.*</c>(API 密钥、地址、开关、健康重置;owner,密钥只给本机通道)。</summary>
    public const string Gateway = "gateway";
}

/// <summary>M4 各领域方法表共用的声明助手:种类、角色、承载、能力在一处写清(路由统一检查,09 §8.4)。</summary>
internal static class MethodTable
{
    public static MethodDef<TP, TR> Query<TP, TR>(string name, Role role, string capability, Transports allowed = Transports.Any) =>
        new(name, MethodKind.Query, role, allowed, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = capability };

    public static MethodDef<TP, TR> Command<TP, TR>(string name, Role role, string capability, Transports allowed = Transports.Any) =>
        new(name, MethodKind.Command, role, allowed, PayloadShapes.GetShape<TP>(), PayloadShapes.GetShape<TR>()) { Capability = capability };
}
