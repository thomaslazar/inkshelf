using System.Net;
using Inkshelf;

namespace Inkshelf.Tests;

public class ForwardedProxiesTests
{
    [Fact]
    public void Parse_null_or_empty_returns_empty()
    {
        var (p, n) = ForwardedProxies.Parse(null);
        Assert.Empty(p); Assert.Empty(n);
        (p, n) = ForwardedProxies.Parse("   ");
        Assert.Empty(p); Assert.Empty(n);
    }

    [Fact]
    public void Parse_splits_ips_and_cidrs()
    {
        var (proxies, networks) = ForwardedProxies.Parse("1.2.3.4, 10.0.0.0/8 , ::1");
        Assert.Contains(IPAddress.Parse("1.2.3.4"), proxies);
        Assert.Contains(IPAddress.Parse("::1"), proxies);
        Assert.Single(networks);
        Assert.Equal(IPNetwork.Parse("10.0.0.0/8"), networks[0]);
    }

    [Fact]
    public void Parse_skips_invalid_entries()
    {
        var (proxies, networks) = ForwardedProxies.Parse("not-an-ip, 1.2.3.4, bad/cidr");
        Assert.Equal(new[] { IPAddress.Parse("1.2.3.4") }, proxies);
        Assert.Empty(networks);
    }
}
