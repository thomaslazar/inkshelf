using Microsoft.AspNetCore.Mvc.Testing;

namespace Inkshelf.Tests;

public class SmokeTests
{
    [Fact]
    public void MissingAbsUrl_FailsStartup()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ABS_URL", ""));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void MalformedAbsUrl_FailsStartup()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ABS_URL", "abs.local:13378"));
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void MalformedAbsPublicUrl_FailsStartup()
    {
        // Otherwise the typo would only surface as a 500 on the first SSO attempt.
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("ABS_PUBLIC_URL", "abs.example.com");
        });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
