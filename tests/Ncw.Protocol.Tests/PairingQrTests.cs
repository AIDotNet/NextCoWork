using Ncw.Protocol.Security;

namespace Ncw.Protocol.Tests;

public sealed class PairingQrTests
{
    private static PairingQr Sample()
    {
        var keys = PeerIdentity.Create("qr-device");
        return new PairingQr(keys.DeviceId, keys.Fingerprint, ["192.168.1.23:7810", "[fe80::1]:7810"], new byte[PairingQr.TokenLength], 1_900_000_000_000);
    }

    [Fact]
    public void Round_trips_through_the_uri()
    {
        var qr = Sample();
        Assert.StartsWith("nextcowork://pair?v=1&", qr.ToUri());
        Assert.True(PairingQr.TryParse(qr.ToUri(), out var parsed));
        Assert.Equal(qr.DeviceId, parsed.DeviceId);
        Assert.Equal(qr.Fingerprint, parsed.Fingerprint);
        Assert.Equal(qr.Addresses, parsed.Addresses);
        Assert.Equal(qr.Token, parsed.Token);
        Assert.Equal(qr.ExpiresAt, parsed.ExpiresAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com/pair?v=1")]
    [InlineData("nextcowork://login?v=1")]
    [InlineData("nextcowork://pair?v=2")]
    [InlineData("nextcowork://pair")]
    public void Rejects_foreign_or_incomplete_content(string content) =>
        Assert.False(PairingQr.TryParse(content, out _));

    [Fact]
    public void Rejects_a_device_id_that_does_not_match_the_fingerprint()
    {
        var uri = (Sample() with { DeviceId = "AAAAAAAAAAAAAAAAAAAAAAAAAA" }).ToUri();
        Assert.False(PairingQr.TryParse(uri, out _));
    }

    [Fact]
    public void Rejects_a_token_of_the_wrong_length_and_bad_addresses()
    {
        Assert.False(PairingQr.TryParse((Sample() with { Token = new byte[8] }).ToUri(), out _));
        Assert.False(PairingQr.TryParse((Sample() with { Addresses = ["no-port"] }).ToUri(), out _));
        Assert.False(PairingQr.TryParse((Sample() with { Addresses = ["host:99999"] }).ToUri(), out _));
        Assert.False(PairingQr.TryParse((Sample() with { Addresses = [] }).ToUri(), out _));
    }

    [Theory]
    [InlineData("10.0.0.2", 7810, "10.0.0.2:7810")]
    [InlineData("fe80::1", 7810, "[fe80::1]:7810")]
    public void Formats_and_splits_addresses(string host, int port, string text)
    {
        Assert.Equal(text, PairingQr.FormatAddress(host, port));
        Assert.True(PairingQr.TrySplitAddress(text, out var h, out var p));
        Assert.Equal((host, port), (h, p));
    }
}
