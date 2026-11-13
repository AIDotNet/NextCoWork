using Ncw.Core.Ids;

namespace Ncw.Core.Tests;

public sealed class UlidTests
{
    [Fact]
    public void Format_matches_2x_and_time_prefix_is_encoded()
    {
        var clock = new FixedClock((1_700_000_000_000));

        var id = Ulid.New(clock);

        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{26}$", id);
        Assert.Equal("01HF7YAT00", id[..10]); // 1700000000000 的 Crockford base32
    }

    [Fact]
    public void Ids_in_the_same_millisecond_are_strictly_increasing()
    {
        var clock = new FixedClock((1_700_000_000_001));

        var ids = Enumerable.Range(0, 1000).Select(_ => Ulid.New(clock)).ToList();

        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Prefixed_id_uses_underscore()
    {
        Assert.StartsWith("ws_", Ulid.Prefixed("ws"));
        Assert.Equal(29, Ulid.Prefixed("ws").Length);
    }
}

file sealed class FixedClock(long unixMs) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
}
