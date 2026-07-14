using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Inkshelf.Abs;
using Inkshelf.Auth;

namespace Inkshelf.Tests;

public class AbsAuthHandlerTests
{
    // Inner handler: returns the scripted status codes in order (last one repeats),
    // recording the Authorization header and body bytes seen on each call.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode[] _statuses;
        private int _i;
        public List<string?> Auths { get; } = new();
        public List<byte[]?> Bodies { get; } = new();
        public List<string> UserAgents { get; } = new();
        public RecordingHandler(params HttpStatusCode[] statuses) => _statuses = statuses;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Auths.Add(r.Headers.Authorization?.ToString());
            UserAgents.Add(r.Headers.UserAgent.ToString());
            Bodies.Add(r.Content is null ? null : await r.Content.ReadAsByteArrayAsync(ct));
            var code = _statuses[Math.Min(_i++, _statuses.Length - 1)];
            return new HttpResponseMessage(code) { Content = new StringContent("body") };
        }
    }

    private sealed class Setup
    {
        public required AbsAuthHandler Handler { get; init; }
        public required DefaultHttpContext Ctx { get; init; }
        public required RecordingHandler Inner { get; init; }
    }

    // refreshStatus drives the AbsAuthClient stub: 200 → returns new tokens; 401 → RefreshAsync throws.
    private static Setup Make(HttpStatusCode[] innerStatuses, bool withToken, HttpStatusCode refreshStatus = HttpStatusCode.OK)
    {
        var dp = DataProtectionProvider.Create("inkshelf-tests");
        var accessor = new HttpContextAccessor();
        var refreshResponder = new Func<HttpRequestMessage, HttpResponseMessage>(_ =>
            refreshStatus == HttpStatusCode.OK
                ? StubHandler.Json("""{"user":{"accessToken":"newacc","refreshToken":"newref"}}""")
                : new HttpResponseMessage(refreshStatus));
        var authClient = new AbsAuthClient(new HttpClient(new StubHandler(refreshResponder))
        { BaseAddress = new Uri("http://abs.local") });

        var services = new ServiceCollection();
        services.AddSingleton<IDataProtectionProvider>(dp);
        services.AddSingleton<IHttpContextAccessor>(accessor);
        services.AddTransient<TokenStore>();          // transient in tests avoids scope ceremony
        services.AddSingleton(authClient);
        var provider = services.BuildServiceProvider();

        var ctx = new DefaultHttpContext { RequestServices = provider };
        // Seed the cookie (if any) BEFORE pointing the shared accessor at ctx.
        // IHttpContextAccessor.HttpContext is backed by a static AsyncLocal shared
        // across every instance in this call context, so the throwaway accessor
        // below — used only to run TokenStore.Save against a scratch context —
        // would otherwise clobber what `accessor` (and thus AbsAuthHandler) sees.
        if (withToken)
        {
            var w = new DefaultHttpContext();
            new TokenStore(dp, new HttpContextAccessor { HttpContext = w }).Save(new Tokens("acc", "ref"));
            var value = w.Response.Headers.SetCookie.ToString().Split(';')[0].Split('=', 2)[1];
            ctx.Request.Headers.Cookie = $"inkshelf_session={value}";
        }
        accessor.HttpContext = ctx;

        var inner = new RecordingHandler(innerStatuses);
        var handler = new AbsAuthHandler(accessor) { InnerHandler = inner };
        return new Setup { Handler = handler, Ctx = ctx, Inner = inner };
    }

    private static Task<HttpResponseMessage> Send(AbsAuthHandler handler, HttpRequestMessage req) =>
        new HttpMessageInvoker(handler).SendAsync(req, default);

    [Fact]
    public async Task Attaches_bearer_and_returns_on_success()
    {
        var s = Make(new[] { HttpStatusCode.OK }, withToken: true);
        var res = await Send(s.Handler, new HttpRequestMessage(HttpMethod.Get, "http://abs.local/x"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Bearer acc", s.Inner.Auths.Single());
    }

    [Fact]
    public async Task Refreshes_then_retries_on_401()
    {
        var s = Make(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.OK }, withToken: true);
        var res = await Send(s.Handler, new HttpRequestMessage(HttpMethod.Get, "http://abs.local/x"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, s.Inner.Auths.Count);
        Assert.Equal("Bearer acc", s.Inner.Auths[0]);
        Assert.Equal("Bearer newacc", s.Inner.Auths[1]);      // retried with refreshed token
        Assert.Contains("inkshelf_session=", s.Ctx.Response.Headers.SetCookie.ToString()); // saved
    }

    [Fact]
    public async Task Throws_AbsAuth_on_double_401()
    {
        var s = Make(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized }, withToken: true);
        await Assert.ThrowsAsync<AbsAuthException>(() =>
            Send(s.Handler, new HttpRequestMessage(HttpMethod.Get, "http://abs.local/x")));
    }

    [Fact]
    public async Task Throws_AbsAuth_when_refresh_fails()
    {
        var s = Make(new[] { HttpStatusCode.Unauthorized }, withToken: true, refreshStatus: HttpStatusCode.Unauthorized);
        await Assert.ThrowsAsync<AbsAuthException>(() =>
            Send(s.Handler, new HttpRequestMessage(HttpMethod.Get, "http://abs.local/x")));
    }

    [Fact]
    public async Task Throws_AbsAuth_when_no_token()
    {
        var s = Make(new[] { HttpStatusCode.OK }, withToken: false);
        await Assert.ThrowsAsync<AbsAuthException>(() =>
            Send(s.Handler, new HttpRequestMessage(HttpMethod.Get, "http://abs.local/x")));
    }

    [Fact]
    public async Task Throws_AbsAuth_when_no_http_context()
    {
        // No request in flight (accessor.HttpContext == null) → auth is impossible.
        var handler = new AbsAuthHandler(new HttpContextAccessor())
        { InnerHandler = new RecordingHandler(HttpStatusCode.OK) };
        await Assert.ThrowsAsync<AbsAuthException>(() =>
            new HttpMessageInvoker(handler).SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "http://abs.local/x"), default));
    }

    [Fact]
    public async Task Preserves_user_agent_across_retry()
    {
        var s = Make(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.OK }, withToken: true);
        var req = new HttpRequestMessage(HttpMethod.Get, "http://abs.local/x");
        req.Headers.UserAgent.ParseAdd("Inkshelf/9.9.9");   // simulate DefaultRequestHeaders applied pre-handler
        await Send(s.Handler, req);
        // The rebuilt retry request must still carry the UA (ABS proxy 403s empty UA).
        Assert.Equal(2, s.Inner.UserAgents.Count);
        Assert.Equal("Inkshelf/9.9.9", s.Inner.UserAgents[1]);
    }

    [Fact]
    public async Task Resends_post_body_on_retry()
    {
        var s = Make(new[] { HttpStatusCode.Unauthorized, HttpStatusCode.OK }, withToken: true);
        var req = new HttpRequestMessage(HttpMethod.Post, "http://abs.local/x")
        {
            Content = new StringContent("""{"libraryItemIds":["i1"]}""",
                System.Text.Encoding.UTF8, "application/json")
        };
        await Send(s.Handler, req);
        Assert.Equal(2, s.Inner.Bodies.Count);
        Assert.NotNull(s.Inner.Bodies[0]);
        Assert.Equal(s.Inner.Bodies[0], s.Inner.Bodies[1]);   // identical body re-sent
    }
}
