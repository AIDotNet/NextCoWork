using Ncw.Core.Domain;

namespace Ncw.Core.Tests;

/// <summary>设备维度什么时候露出来(docs/design/03 §11.7)。</summary>
public class DeviceDimensionTests
{
    [Theory]
    [InlineData(1, false, false, false)] // 只有本机:不画
    [InlineData(2, false, false, true)]  // 两台
    [InlineData(1, true, false, true)]   // 唯一的设备是远程 / 云端
    [InlineData(1, false, true, true)]   // 有 SSH 环境里的项目
    public void Shows_only_when_there_is_more_than_the_local_machine(int count, bool remote, bool ssh, bool expected) =>
        Assert.Equal(expected, DeviceDimension.Shows(count, remote, ssh));

    [Fact]
    public void Scope_details_still_show_when_something_is_wrong_or_you_are_not_the_owner()
    {
        Assert.False(DeviceDimension.ShowsScopeDetails(false, DeviceConnection.Connected, "owner"));
        Assert.True(DeviceDimension.ShowsScopeDetails(false, DeviceConnection.Offline, "owner"));
        Assert.True(DeviceDimension.ShowsScopeDetails(false, DeviceConnection.Connecting, null));
        Assert.True(DeviceDimension.ShowsScopeDetails(false, DeviceConnection.Connected, "viewer"));
        Assert.True(DeviceDimension.ShowsScopeDetails(true, DeviceConnection.Connected, "owner"));
    }
}
