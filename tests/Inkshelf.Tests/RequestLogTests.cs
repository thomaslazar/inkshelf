using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inkshelf.Tests;

// The request log exists to make device problems diagnosable, so what matters is
// that the line carries the two things that were missing before it: the query (which
// geometry, which item, which settings a bookmark held) and the bytes actually
// delivered (a truncated download differs from a complete one only there).
public class RequestLogTests
{
    // Captures what the app logged, so the assertions read the real line rather than
    // a mock's idea of it.
    private sealed class Capture : ILoggerProvider
    {
        public readonly List<string> Lines = [];
        public ILogger CreateLogger(string category) => new Sink(this, category);
        public void Dispose() { }

        private sealed class Sink(Capture owner, string category) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Noop();
            public bool IsEnabled(LogLevel level) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
                Func<TState, Exception?, string> formatter)
            {
                if (category == "Inkshelf.Request") owner.Lines.Add(formatter(state, ex));
            }
            private sealed class Noop : IDisposable { public void Dispose() { } }
        }
    }

    private static WebApplicationFactory<Program> CreateFactory(Capture capture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://localhost:1");
            b.ConfigureServices(svc => svc.AddSingleton<ILoggerProvider>(capture));
        });

    [Fact]
    public async Task Logs_one_line_per_request_with_the_query_and_the_bytes_written()
    {
        var capture = new Capture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient();

        var body = await (await client.GetAsync("/settings?ovr=1&ovrw=1120")).Content.ReadAsStringAsync();

        var line = Assert.Single(capture.Lines, l => l.Contains("/settings"));
        Assert.Contains("GET /settings?ovr=1&ovrw=1120", line);   // the query is the diagnosis
        Assert.Contains(" 200 ", line);
        // Bytes are what the app wrote, which for a complete response equals the body
        // we received — and a complete response must NOT be flagged as short.
        Assert.Contains($" {System.Text.Encoding.UTF8.GetByteCount(body)}b ", line);
        Assert.DoesNotContain("INCOMPLETE", line);
    }

    [Fact]
    public async Task Logs_a_request_that_a_later_middleware_answered_itself()
    {
        // The auth catch-all short-circuits a cookie-less file request with a 401. That
        // path never reaches an endpoint, and it is exactly the case that was invisible
        // when a reader's download came back unreadable.
        var capture = new Capture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.GetAsync("/download/abc123");

        var line = Assert.Single(capture.Lines, l => l.Contains("/download/abc123"));
        Assert.Contains("GET /download/abc123 401", line);
        Assert.DoesNotContain(" 0b ", line);   // the plain-text body was delivered
    }

    [Fact]
    public async Task Logs_NOCOOKIE_for_a_cookie_less_file_request()
    {
        var capture = new Capture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.GetAsync("/download/abc123");

        var line = Assert.Single(capture.Lines, l => l.Contains("/download/abc123"));
        Assert.Contains("NOCOOKIE", line);
    }

    [Fact]
    public async Task Does_not_log_NOCOOKIE_when_a_session_cookie_is_present()
    {
        var capture = new Capture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add("Cookie", "inkshelf_session=whatever");

        await client.GetAsync("/download/abc123");

        var line = Assert.Single(capture.Lines, l => l.Contains("/download/abc123"));
        Assert.DoesNotContain("NOCOOKIE", line);
    }

    [Fact]
    public async Task Does_not_log_NOCOOKIE_for_a_cookie_less_page_request()
    {
        // Pins the scoping to NonHtmlEndpoint: a logged-out page hit is not
        // interesting and must not carry the marker.
        var capture = new Capture();
        using var factory = CreateFactory(capture);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.GetAsync("/settings");

        var line = Assert.Single(capture.Lines, l => l.Contains("/settings"));
        Assert.DoesNotContain("NOCOOKIE", line);
    }

    [Fact]
    public void The_logged_query_keeps_everything_but_the_ticket()
    {
        Assert.Equal("?file=2&t=…&return=%2F",
            RequestLog.Redact("?file=2&t=Ab_1Cd-2Ef3Gh4Ij5Kl6&return=%2F"));
        Assert.Equal("?t=…", RequestLog.Redact("?t=Ab_1Cd-2Ef3Gh4Ij5Kl6"));
        Assert.Equal("?sort=t", RequestLog.Redact("?sort=t"));   // not a ticket
        Assert.Equal("", RequestLog.Redact(""));
        Assert.Null(RequestLog.Redact(null));
    }
}
