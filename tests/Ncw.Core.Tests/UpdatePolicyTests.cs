using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>强制更新判定(docs/design/04 §3):逐条对应 2.x <c>update-service.ts</c> 的 <c>canStartNewRuns</c>,时钟由测试传入。</summary>
public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("3.0.1", "3.0.0", true)]
    [InlineData("3.0.0", "3.0.0", false)]
    [InlineData("3.0.0", "3.0.1", false)]
    [InlineData("3.0.0", "3.0.0-beta.2", true)]
    [InlineData("3.0.0-beta.2", "3.0.0-beta.11", false)]   // 数字标识符按数值比,不是字符串
    [InlineData("3.0.0-beta.11", "3.0.0-beta.2", true)]
    [InlineData("3.0.0-alpha.1", "3.0.0-alpha", true)]
    [InlineData("3.0.0-rc.1", "3.0.0-beta.9", true)]
    [InlineData("3.0.0-1", "3.0.0-alpha", false)]          // 数字标识符 < 字母标识符
    [InlineData("3.0.0+abc", "3.0.0+xyz", false)]          // 构建元数据不参与比较
    [InlineData("v3.1.0", "3.0.9", true)]
    [InlineData("3.10.0", "3.9.0", true)]
    [InlineData("3.0.0.1", "3.0.0", true)]                  // Velopack 允许第四段
    public void IsNewer_follows_semver_precedence(string candidate, string current, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.IsNewer(candidate, current));

    [Theory]
    [InlineData("", "3.0.0")]
    [InlineData("3.0.1", "")]
    [InlineData("abc", "3.0.0")]
    [InlineData("3", "3.0.0")]
    [InlineData("3.0.0-", "3.0.0")]
    [InlineData(null, "3.0.0")]
    public void Unparsable_versions_are_never_newer(string? candidate, string? current) =>
        Assert.False(UpdatePolicy.IsNewer(candidate, current));

    [Fact]
    public void No_update_or_optional_update_never_constrains()
    {
        Assert.Equal(UpdateEnforcement.None, UpdatePolicy.EnforcementOf("3.0.0", null, Now));
        Assert.Equal(UpdateEnforcement.None, UpdatePolicy.EnforcementOf("3.0.0", new UpdateRelease("3.1.0"), Now));
        // 可选更新即使带了最低版本也不阻断(只有 mandatory 才有约束力)
        Assert.Equal(UpdateEnforcement.None, UpdatePolicy.EnforcementOf("2.0.0", new UpdateRelease("3.1.0", false, "3.0.5"), Now));
        Assert.True(UpdatePolicy.CanStartNewRuns("2.0.0", new UpdateRelease("3.1.0", false, "3.0.5"), Now));
    }

    [Fact]
    public void Mandatory_without_a_minimum_version_is_required_but_does_not_block()
    {
        var release = new UpdateRelease("3.1.0", Mandatory: true);
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("3.0.0", release, Now));
        Assert.True(UpdatePolicy.CanStartNewRuns("3.0.0", release, Now));
    }

    [Fact]
    public void Mandatory_is_required_when_the_current_version_already_meets_the_minimum()
    {
        var release = new UpdateRelease("3.1.0", true, "3.0.5");
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("3.0.5", release, Now));
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("3.0.9", release, Now));
        Assert.True(UpdatePolicy.CanStartNewRuns("3.0.5", release, Now));
    }

    [Fact]
    public void Mandatory_below_the_minimum_blocks_once_the_grace_period_is_over()
    {
        var release = new UpdateRelease("3.1.0", true, "3.0.5", Now.AddHours(1));
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("3.0.0", release, Now));          // 宽限期内
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("3.0.0", release, Now.AddMinutes(59)));
        Assert.Equal(UpdateEnforcement.Blocking, UpdatePolicy.EnforcementOf("3.0.0", release, Now.AddHours(1)));  // 恰好到点:已过
        Assert.Equal(UpdateEnforcement.Blocking, UpdatePolicy.EnforcementOf("3.0.0", release, Now.AddDays(3)));
        Assert.False(UpdatePolicy.CanStartNewRuns("3.0.0", release, Now.AddDays(3)));
    }

    [Fact]
    public void Mandatory_below_the_minimum_without_a_grace_date_blocks_immediately()
    {
        var release = new UpdateRelease("3.1.0", true, "3.0.5");
        Assert.Equal(UpdateEnforcement.Blocking, UpdatePolicy.EnforcementOf("3.0.0", release, Now));
        Assert.False(UpdatePolicy.CanStartNewRuns("3.0.0", release, Now));
    }

    [Fact]
    public void A_past_grace_date_does_not_unblock_and_a_clock_going_back_re_enters_the_grace_period()
    {
        var release = new UpdateRelease("3.1.0", true, "3.0.5", Now);
        Assert.Equal(UpdateEnforcement.Blocking, UpdatePolicy.EnforcementOf("3.0.0", release, Now.AddSeconds(1)));
        // 时钟是调用方给的:传回更早的时间就又在宽限期里(调用方负责用可信时钟)
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("3.0.0", release, Now.AddSeconds(-1)));
    }

    [Fact]
    public void Prerelease_current_versions_compare_below_the_final_minimum()
    {
        var release = new UpdateRelease("3.0.0", true, "3.0.0");
        Assert.Equal(UpdateEnforcement.Blocking, UpdatePolicy.EnforcementOf("3.0.0-alpha.1", release, Now));
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("3.0.0", release, Now));
    }

    [Fact]
    public void Unparsable_versions_do_not_block()
    {
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("dev", new UpdateRelease("3.1.0", true, "3.0.5"), Now));
        Assert.Equal(UpdateEnforcement.Required, UpdatePolicy.EnforcementOf("3.0.0", new UpdateRelease("3.1.0", true, "oops"), Now));
    }

    [Fact]
    public void A_manifest_whose_minimum_is_above_its_own_version_is_incoherent()
    {
        Assert.True(UpdatePolicy.IsCoherent(new UpdateRelease("3.1.0", true, "3.1.0")));
        Assert.True(UpdatePolicy.IsCoherent(new UpdateRelease("3.1.0", false, null)));
        Assert.False(UpdatePolicy.IsCoherent(new UpdateRelease("3.1.0", true, "3.2.0")));
        Assert.False(UpdatePolicy.IsCoherent(new UpdateRelease("3.1.0", true, "x")));
        Assert.False(UpdatePolicy.IsCoherent(new UpdateRelease("nope")));
    }
}
