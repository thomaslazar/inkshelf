using System.Net;

namespace Inkshelf;

// Parses a comma-separated TRUSTED_PROXY value into bare IPs (→ KnownProxies) and
// CIDR ranges (→ KnownIPNetworks) for ForwardedHeadersOptions. Invalid entries are
// skipped. Empty/null input yields empty lists (caller then trusts all hops).
public static class ForwardedProxies
{
    public static (List<IPAddress> Proxies, List<IPNetwork> Networks) Parse(string? trustedProxy)
    {
        var proxies = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        if (string.IsNullOrWhiteSpace(trustedProxy)) return (proxies, networks);
        foreach (var raw in trustedProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Contains('/'))
            {
                if (IPNetwork.TryParse(raw, out var net)) networks.Add(net);
            }
            else if (IPAddress.TryParse(raw, out var ip))
            {
                proxies.Add(ip);
            }
        }
        return (proxies, networks);
    }
}
