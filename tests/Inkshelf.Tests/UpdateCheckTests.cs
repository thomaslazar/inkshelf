using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inkshelf.Tests;

public class UpdateCheckTests
{
    [Theory]
    // A newer release is the only case that renders anything.
    [InlineData("1.0.0", "v1.0.1", "1.0.1")]
    [InlineData("1.0.0", "1.0.1", "1.0.1")]
    // A PR image is stamped "<version>+pr-34.a1b2c3d"; it compares as its base version.
    [InlineData("1.0.0+pr-34.a1b2c3d", "v1.0.1", "1.0.1")]
    // Up to date, and ahead of the latest release (a local build), both stay silent.
    [InlineData("1.0.0", "v1.0.0", null)]
    [InlineData("1.1.0", "v1.0.1", null)]
    // Anything that will not parse shows nothing rather than guessing.
    [InlineData("1.0.0", "nightly", null)]
    [InlineData("1.0.0", "", null)]
    [InlineData("1.0.0", "v1.1.0-rc1", null)]
    [InlineData("not-a-version", "v1.0.1", null)]
    public void NewerThan_reports_only_a_parseable_newer_release(string local, string remote, string? expected)
        => Assert.Equal(expected, UpdateCheck.NewerThan(local, remote));

    // The fetch itself: the field name we read out of the GitHub response, and
    // that Poke() eventually lands a result without the caller awaiting it.
    private static (UpdateCheck Check, StubHandler Stub) Build(bool enabled, HttpResponseMessage response)
    {
        var stub = new StubHandler(_ => response);
        var services = new ServiceCollection();
        services.AddHttpClient("github");
        services.Configure<HttpClientFactoryOptions>("github", o =>
            o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = stub));
        var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
        return (new UpdateCheck(factory, new AbsOptions { UpdateCheck = enabled }, NullLogger<UpdateCheck>.Instance), stub);
    }

    private static async Task<string?> Settled(UpdateCheck check)
    {
        for (var i = 0; i < 100 && check.Newer is null; i++) await Task.Delay(10);
        return check.Newer;
    }

    [Fact]
    public async Task A_release_newer_than_ours_is_read_out_of_the_response()
    {
        var (check, stub) = Build(enabled: true, StubHandler.Json("""{"tag_name":"v999.0.0","name":"whatever"}"""));

        check.Poke();

        Assert.Equal("999.0.0", await Settled(check));
        Assert.Equal("https://api.github.com/repos/thomaslazar/inkshelf/releases/latest", stub.Last!.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_failed_check_is_swallowed()
    {
        // No network, rate limiting and a shape change all have to mean "no hint",
        // never an exception escaping into the render that poked it.
        var (check, _) = Build(enabled: true, StubHandler.Json("nonsense", System.Net.HttpStatusCode.ServiceUnavailable));

        check.Poke();
        await Task.Delay(100);

        Assert.Null(check.Newer);
    }

    [Fact]
    public void Disabled_means_no_request_at_all()
    {
        var (check, stub) = Build(enabled: false, StubHandler.Json("""{"tag_name":"v999.0.0"}"""));

        check.Poke();

        Assert.Null(stub.Last);
    }
}
