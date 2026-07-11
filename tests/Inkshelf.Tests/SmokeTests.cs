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
}
