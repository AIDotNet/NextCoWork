using System.Collections.Immutable;
using System.Diagnostics;
using Ncw.Client.Actions;
using Ncw.Client.Mock;
using Ncw.Client.Services;
using Ncw.Client.Stores;
using Ncw.Core.Domain;
using Ncw.Protocol.Methods;
using Ncw.Protocol.Security;
using static Ncw.UI.Tests.ConversationFixtures;

namespace Ncw.UI.Tests;

/// <summary>时间由测试推进的时钟:<c>GetUtcNow</c> 固定在 <see cref="ConversationFixtures.Now"/> + 已推进的时间,<c>GetTimestamp</c> 也按它走(长按手势用)。</summary>
public sealed class ManualClock : TimeProvider
{
    private long _timestamp = 1_000;
    private TimeSpan _advanced;

    public void Advance(TimeSpan by)
    {
        _advanced += by;
        _timestamp += (long)(by.TotalSeconds * Stopwatch.Frequency);
    }

    public override DateTimeOffset GetUtcNow() => (Now + _advanced).ToUniversalTime();

    public override long GetTimestamp() => _timestamp;
}

/// <summary>扫码器的脚本化假实现。</summary>
public sealed class FakeQrScanner(bool supported = true, CameraAccess access = CameraAccess.Granted) : IQrScanner
{
    public TaskCompletionSource<string?> Scan { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsSupported { get; } = supported;

    public Task<CameraAccess> RequestAccessAsync(CancellationToken ct = default) => Task.FromResult(access);

    public Task<string?> ScanAsync(CancellationToken ct = default)
    {
        ct.Register(() => Scan.TrySetResult(null));
        return Scan.Task;
    }
}

/// <summary>附件选择的假实现:返回预先放好的附件。</summary>
public sealed class FakePicker(params PickedAttachment[] files) : IAttachmentPicker
{
    public bool IsAvailable(AttachmentSource source) => true;

    public Task<IReadOnlyList<PickedAttachment>> PickAsync(AttachmentSource source, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PickedAttachment>>(files);
}

/// <summary>上传的假实现:每个附件等测试放行(<see cref="Gate"/>)后才返回,期间可以报告进度。</summary>
public sealed class FakeUploader : IAttachmentUploader
{
    public TaskCompletionSource<ActionResult<AttachmentDto>> Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>每个附件(按显示名)的进度回调。</summary>
    public Dictionary<string, IProgress<long>?> ProgressOf { get; } = [];

    public IProgress<long>? Progress => ProgressOf.Values.FirstOrDefault();

    public Task<ActionResult<AttachmentDto>> UploadAsync(string deviceId, string sessionId, string displayName, string mime, byte[] bytes, IProgress<long>? progress, CancellationToken ct = default)
    {
        ProgressOf[displayName] = progress;
        return Gate.Task;
    }

    public Task<ActionResult<Unit>> RemoveAsync(string deviceId, string attachmentId, CancellationToken ct = default) =>
        Task.FromResult(new ActionResult<Unit>(ActionOutcome.Ok, Unit.Value));
}

/// <summary>手机壳截图测试用的 Store 数据:在 Mock 的设备 / 项目之上写入交互、已结束的 Run、设备连接状态。形状和设备推来的完全一致。</summary>
public static class MobileFixtures
{
    public const string Studio = "studio";

    /// <summary>三台设备:本机(连着)、云端(休眠)、工作室(离线)。</summary>
    public static ClientStores Base()
    {
        var stores = MockWorkspace.Create(Now);
        stores.Workspace.Update(w => w with { Devices = w.Devices.SetItem("local", w.Devices["local"] with { Role = "owner" }) });
        return stores;
    }

    /// <summary>所有设备都连着、没有任何待办:收件箱是空的。</summary>
    public static ClientStores Quiet()
    {
        var stores = Base();
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem("cloud", w.Devices["cloud"] with { Connection = DeviceConnection.Connected, Role = "owner" })
                .SetItem(Studio, w.Devices[Studio] with { Connection = DeviceConnection.Connected, Role = "member" }),
        });
        return stores;
    }

    public static void Interactions(ClientStores stores, string device, long revision, params InteractionDto[] pending) =>
        stores.Conversations.Update(c => ConversationReducer.ApplyInteractions(c, device, new InteractionsDto(revision, pending)));

    public static ToolPermissionInteractionDto Approval(string device, string id, string runId, string tool, string input, string env = "local", bool dangerous = false, int secondsAgo = 30) => new()
    {
        Id = id,
        RunId = runId,
        CallId = "c-" + id,
        ToolName = tool,
        InternalId = tool.ToLowerInvariant(),
        InputJson = input,
        Env = new EnvRefDto(device, env, env, env == "local" ? "local" : "ssh"),
        AllowedScopes = ["session", "project", "global"],
        Risk = dangerous ? "dangerous" : "unknown",
        CreatedAt = Ms(Now.AddSeconds(-secondsAgo)),
        Version = 12,
    };

    /// <summary>本机有待办:一个危险的命令审批、一个提问、一个计划;外加一条已完成、一条失败的 Run 通知。</summary>
    public static ClientStores Pending()
    {
        var stores = Quiet();
        Interactions(stores, "local", 9,
            Approval("local", "i1", "run-1", "Bash", "{\"command\":\"rm -rf out/ && dotnet publish -c Release\"}", dangerous: true, secondsAgo: 40),
            new AskUserInteractionDto
            {
                Id = "i2", RunId = "run-1", CreatedAt = Ms(Now.AddSeconds(-90)), Version = 13,
                Questions = [new AskUserQuestionDto("发布目标", "要发布到哪个平台?", [new AskUserOptionDto("osx-arm64"), new AskUserOptionDto("win-x64")], false, false)],
            },
            new PlanApprovalInteractionDto { Id = "i3", RunId = "run-2", CreatedAt = Ms(Now.AddMinutes(-5)), Version = 14, PlanId = "p1", Path = "plan.md", Plan = "## 计划\n\n1. 抽出会话过滤\n2. 补测试" });
        Finished(stores, "local", "run-done", "s-ncw-2", RunPhase.Done);
        Finished(stores, "local", "run-bad", "s-oct-1", RunPhase.Error);
        return stores;
    }

    /// <summary>多设备:本机、工作室(连着,一个 SSH 环境里的写文件审批)、云端(连着,一个目标提案)。</summary>
    public static ClientStores MultiDevice()
    {
        var stores = Pending();
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem(Studio, w.Devices[Studio] with
            {
                Index = new ActiveRunIndex(Studio, [new ActiveRun(Studio, "run-s1", "s-infra-1", "infra", 1)]),
            }),
            Sessions = w.Sessions.Add(new SessionSummary("s-infra-1", new ProjectRef(Studio, "infra"), "升级 Terraform 模块", Now.AddMinutes(-4))),
        });
        Interactions(stores, Studio, 3, Approval(Studio, "s1", "run-s1", "Write", "{\"file_path\":\"/Users/ops/infra/main.tf\",\"content\":\"…\"}", env: "ops-vpn", secondsAgo: 120));
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem("cloud", w.Devices["cloud"] with { Index = new ActiveRunIndex("cloud", [new ActiveRun("cloud", "run-c1", "s-blog-1", "blog", 1)]) }),
        });
        Interactions(stores, "cloud", 2, new GoalProposalInteractionDto { Id = "g1", RunId = "run-c1", CreatedAt = Ms(Now.AddMinutes(-9)), Version = 4, SessionId = "s-blog-1", Condition = "文章发布前通过拼写检查并且图片都有 alt 文字" });
        return stores;
    }

    /// <summary>工作室离线:它上次的待办还在(状态未知,灰显、不计入角标),云端休眠也是未知。</summary>
    public static ClientStores Offline()
    {
        var stores = Pending();
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem(Studio, w.Devices[Studio] with
            {
                Index = new ActiveRunIndex(Studio, [new ActiveRun(Studio, "run-s1", "s-infra-1", "infra", 1)]),
            }),
            Sessions = w.Sessions.Add(new SessionSummary("s-infra-1", new ProjectRef(Studio, "infra"), "升级 Terraform 模块", Now.AddMinutes(-4))),
        });
        Interactions(stores, Studio, 3, Approval(Studio, "s1", "run-s1", "Write", "{\"file_path\":\"/Users/ops/infra/main.tf\",\"content\":\"…\"}", env: "ops-vpn", secondsAgo: 120));
        stores.Workspace.Update(w => w with
        {
            Devices = w.Devices.SetItem(Studio, w.Devices[Studio] with { Connection = DeviceConnection.Offline }),
        });
        return stores;
    }

    public static void Finished(ClientStores stores, string device, string runId, string sessionId, RunPhase phase) =>
        stores.Conversations.Update(c =>
        {
            var run = new RunRef(device, runId);
            var view = RunView.New(run, sessionId, 1) with { Phase = phase, Settled = true };
            return c with { Runs = c.Runs.SetItem(run, view) };
        });

    /// <summary>会话页:一个危险的命令审批 + 一个普通的写文件审批,Run 在跑。</summary>
    public static ClientStores SessionWithApproval()
    {
        var stores = ConversationFixtures.Base("s-ncw-1");
        Session(stores, "s-ncw-1",
        [
            User("u1", "清理构建产物并重新发布。"),
            Assistant("a1", 3, Text("需要运行几个命令,先确认一下。"), Call("c1", "Bash", "{\"command\":\"rm -rf out/ && dotnet publish -c Release\"}")),
        ], activeRunId: "run-1");
        Run(stores, "run-1", "s-ncw-1", v => v);
        ConversationFixtures.Interactions(stores, ConversationFixtures.Approval("i1", "run-1", "c1", "Bash", "{\"command\":\"rm -rf out/ && dotnet publish -c Release\"}", LocalEnv, dangerous: true));
        return stores;
    }

    /// <summary>一个二维码(10 分钟后过期)。</summary>
    public static string Qr(DateTimeOffset expires)
    {
        var identity = PeerIdentity.Create("mac");
        return new PairingQr(identity.DeviceId, identity.Fingerprint, ["192.168.1.23:7810", "[fe80::1]:7810"], new byte[PairingQr.TokenLength], expires.ToUnixTimeMilliseconds()).ToUri();
    }
}
