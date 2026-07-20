using Inkshelf.Abs;

namespace Inkshelf.Tests;

public class AbsFilterTests
{
    [Fact]
    public void Encode_produces_group_dot_base64()
    {
        // base64("auth-1") = "YXV0aC0x"
        Assert.Equal("authors.YXV0aC0x", AbsFilter.Encode("authors", "auth-1"));
    }

    [Fact]
    public void Decode_round_trips_encode()
    {
        var d = AbsFilter.Decode(AbsFilter.Encode("series", "s1"));
        Assert.NotNull(d);
        Assert.Equal("series", d!.Value.Group);
        Assert.Equal("s1", d.Value.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("series")]          // no dot
    [InlineData("series.")]         // empty value
    [InlineData("series.__none__")] // non-base64 value
    public void Decode_returns_null_for_non_facet(string? filter)
    {
        Assert.Null(AbsFilter.Decode(filter));
    }
}
