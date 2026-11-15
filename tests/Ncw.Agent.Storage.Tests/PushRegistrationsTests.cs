using Ncw.Agent.Storage;

namespace Ncw.Agent.Storage.Tests;

/// <summary>推送令牌登记表(kv <c>v3.push.registrations</c>):按控制端覆盖、上限淘汰最久没更新的、坏数据当没有、吊销的控制端不是发送目标。</summary>
public sealed class PushRegistrationsTests : SessionTestBase
{
    [Fact]
    public void Registering_the_same_controller_again_replaces_its_token_and_survives_a_restart()
    {
        var push = new PushRegistrations(Db);
        push.Put("aa", "apns", "t1", null, 1);
        push.Put("aa", "apns", "t2", "3.0.1", 2);

        Restart();
        var read = new PushRegistrations(Db).List();
        var only = Assert.Single(read);
        Assert.Equal(("aa", "t2", "3.0.1", 2L), (only.ControllerId, only.Token, only.AppVersion, only.UpdatedAt));
    }

    [Fact]
    public void The_table_is_capped_and_drops_the_least_recently_updated_registrations()
    {
        var push = new PushRegistrations(Db);
        for (var i = 0; i < PushRegistrations.MaxRegistrations + 5; i++)
        {
            push.Put($"c{i}", "fcm", $"t{i}", null, i);
        }

        var list = push.List();
        Assert.Equal(PushRegistrations.MaxRegistrations, list.Count);
        Assert.Null(push.Get("c0"));
        Assert.NotNull(push.Get($"c{PushRegistrations.MaxRegistrations + 4}"));
    }

    [Fact]
    public void Corrupt_stored_json_reads_as_no_registrations_and_the_next_registration_repairs_it()
    {
        new KvStore(Db).Set(PushRegistrations.Key, "{ not json");
        var push = new PushRegistrations(Db);

        Assert.Empty(push.List());
        push.Put("aa", "apns", "t", null, 1);
        Assert.Single(push.List());
    }

    [Fact]
    public void Only_registrations_of_controllers_that_are_still_trusted_are_send_targets()
    {
        var trust = new ControllerTrustRepository(Db);
        trust.Put(new ControllerTrust("trusted", "Phone", "member", 1, null), "hash");
        var push = new PushRegistrations(Db);
        push.Put("trusted", "apns", "keep", null, 1);
        push.Put("revoked", "apns", "drop", null, 2);
        push.Put(PushRegistrations.LocalController, "apns", "desktop", null, 3);

        Assert.Equal(["keep"], push.ActiveFor(trust).Select(r => r.Token));
        Assert.True(push.Remove("trusted"));
        Assert.False(push.Remove("trusted"));
    }
}
