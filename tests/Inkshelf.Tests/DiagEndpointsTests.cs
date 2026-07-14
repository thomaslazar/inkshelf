using Microsoft.AspNetCore.Mvc.Testing;
using Inkshelf.Endpoints;

namespace Inkshelf.Tests;

public class DiagEndpointsTests
{
    [Fact]
    public void SanitizeProbe_replaces_control_chars_but_keeps_printable()
    {
        var cleaned = DiagEndpoints.SanitizeProbe("line1\r\nFAKE LOG\tend");
        Assert.DoesNotContain('\n', cleaned);
        Assert.DoesNotContain('\r', cleaned);
        Assert.DoesNotContain('\t', cleaned);
        Assert.Contains("line1", cleaned);
        Assert.Contains("FAKE LOG", cleaned); // printable content (incl. spaces) kept
    }

    [Fact]
    public void SanitizeProbe_truncates_to_cap()
    {
        var cleaned = DiagEndpoints.SanitizeProbe(new string('a', 10_000));
        Assert.True(cleaned.Length <= 4096, $"expected <=4096, got {cleaned.Length}");
    }

    [Fact]
    public async Task Diag_returns_404_when_disabled()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => { b.UseSetting("ABS_URL", "http://localhost:1"); b.UseSetting("DIAG_ENABLED", "false"); });
        using var client = factory.CreateClient();
        var res = await client.PostAsync("/diag", new StringContent("probe"));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Diag_accepts_probe_when_enabled()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ABS_URL", "http://localhost:1"));
        using var client = factory.CreateClient();
        var res = await client.PostAsync("/diag", new StringContent("probe"));
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
    }
}
