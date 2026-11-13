using Ncw.Client.Actions;

namespace Ncw.Client.Tests;

public sealed class DeviceAddressTests
{
    [Theory]
    [InlineData("192.168.1.23:7810", "192.168.1.23", 7810)]
    [InlineData(" 10.0.0.5:9000 ", "10.0.0.5", 9000)]
    [InlineData("192.168.1.23", "192.168.1.23", 7810)]
    [InlineData("studio.local:7811", "studio.local", 7811)]
    [InlineData("[fe80::1]:7810", "fe80::1", 7810)]
    [InlineData("[fd12::5]", "fd12::5", 7810)]
    [InlineData("fd12::5", "fd12::5", 7810)]
    public void Parses_addresses(string text, string host, int port)
    {
        Assert.True(DeviceActions.TryParseAddress(text, out var h, out var p));
        Assert.Equal(host, h);
        Assert.Equal(port, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("192.168.1.23:0")]
    [InlineData("192.168.1.23:70000")]
    [InlineData("192.168.1.23:abc")]
    [InlineData("[fe80::1")]
    [InlineData(":7810")]
    [InlineData("my host:7810")]
    public void Rejects_bad_addresses(string text)
    {
        Assert.False(DeviceActions.TryParseAddress(text, out _, out _));
    }
}
