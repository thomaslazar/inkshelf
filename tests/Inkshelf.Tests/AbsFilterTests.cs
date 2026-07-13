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
}
