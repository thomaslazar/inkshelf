using System.Net;
using Inkshelf.Abs;
using Inkshelf.Endpoints;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Inkshelf.Tests;

public class OidcEndpointTests
{
    // Stands in for ABS across both OIDC legs, recording what each leg received.
    private sealed class AbsStub : HttpMessageHandler
    {
        public readonly List<Uri> Requests = [];
        public readonly List<string> States = [];
        public readonly List<string> Challenges = [];
        public readonly List<string> RedirectUris = [];
        public readonly List<string> ExchangeCookies = [];
        public readonly List<string> Verifiers = [];
        public HttpStatusCode Leg1Status = HttpStatusCode.Found;
        public HttpStatusCode Leg3Status = HttpStatusCode.OK;
        private int _sid;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            Requests.Add(req.RequestUri!);
            var q = QueryHelpers.ParseQuery(req.RequestUri!.Query);

            if (req.RequestUri!.AbsolutePath == "/auth/openid")
            {
                if (Leg1Status != HttpStatusCode.Found)
                    return Task.FromResult(new HttpResponseMessage(Leg1Status)
                    {
                        Content = new StringContent("Invalid redirect_uri")
                    });

                States.Add(q["state"]!);
                Challenges.Add(q["code_challenge"]!);
                RedirectUris.Add(q["redirect_uri"]!);
                var res = new HttpResponseMessage(HttpStatusCode.Found);
                res.Headers.Location = new Uri("https://idp.example/authorize?x=1");
                res.Headers.Add("Set-Cookie", $"connect.sid=s%3A{++_sid}; Path=/; HttpOnly");
                res.Headers.Add("Set-Cookie", "auth_method=openid-mobile; Path=/; HttpOnly");
                return Task.FromResult(res);
            }

            ExchangeCookies.Add(req.Headers.GetValues("Cookie").Single());
            Verifiers.Add(q["code_verifier"]!);
            if (Leg3Status != HttpStatusCode.OK)
                return Task.FromResult(new HttpResponseMessage(Leg3Status)
                {
                    Content = new StringContent("Unauthorized")
                });
            return Task.FromResult(StubHandler.Json(
                """{"user":{"accessToken":"acc","refreshToken":"ref"}}"""));
        }
    }

    private static WebApplicationFactory<Program> Factory(
        HttpMessageHandler? abs = null, bool enabled = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            if (enabled) b.UseSetting("OIDC_ENABLED", "true");
            // Last primary-handler registration wins, so this stubs out ABS.
            if (abs is not null)
                b.ConfigureServices(s => s.AddHttpClient<AbsAuthClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => abs));
        });

    private static HttpClient NoRedirects(WebApplicationFactory<Program> f) =>
        f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static IEnumerable<string> SetCookies(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Set-Cookie", out var v) ? v : [];

    [Fact]
    public void Challenge_is_unpadded_base64url_sha256_of_the_verifier() =>
        // RFC 7636 Appendix B vector. A wrong derivation here looks like a
        // provider misconfiguration rather than our bug, so pin it.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            OidcEndpoints.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public async Task Endpoints_are_404_when_disabled()
    {
        using var f = Factory(enabled: false);
        using var c = NoRedirects(f);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/oidc/start")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.GetAsync("/oidc/callback?code=x&state=y")).StatusCode);
    }

    [Fact]
    public async Task Start_redirects_to_the_provider_and_sets_the_flow_cookie()
    {
        var abs = new AbsStub();
        using var f = Factory(abs);
        using var c = NoRedirects(f);

        var res = await c.GetAsync("/oidc/start");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("https://idp.example/authorize?x=1", res.Headers.Location?.ToString());
        Assert.Contains(SetCookies(res), v => v.StartsWith("inkshelf_oidc="));
        Assert.Equal("http://localhost/oidc/callback", abs.RedirectUris.Single());
    }

    [Fact]
    public async Task Start_on_abs_400_redirects_to_login_error_without_a_flow_cookie()
    {
        var abs = new AbsStub { Leg1Status = HttpStatusCode.BadRequest };
        using var f = Factory(abs);
        using var c = NoRedirects(f);

        var res = await c.GetAsync("/oidc/start");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/login?error=sso", res.Headers.Location?.ToString());
        // ABS's own text stays in the log; nothing is handed to the browser.
        Assert.DoesNotContain(SetCookies(res), v => v.StartsWith("inkshelf_oidc="));
    }

    [Fact]
    public async Task Callback_exchanges_the_code_and_sets_the_session_cookie()
    {
        var abs = new AbsStub();
        using var f = Factory(abs);
        using var c = NoRedirects(f); // carries cookies between the two calls

        await c.GetAsync("/oidc/start");
        var res = await c.GetAsync($"/oidc/callback?code=the-code&state={abs.States.Single()}");

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location?.ToString());
        Assert.Contains(SetCookies(res), v => v.StartsWith("inkshelf_session="));
        // Both ABS cookies from leg 1 came back: without connect.sid ABS answers
        // "No session", and without auth_method it redirects instead of returning tokens.
        Assert.Equal("connect.sid=s%3A1; auth_method=openid-mobile",
            abs.ExchangeCookies.Single());
        // The verifier we sent must match the challenge from leg 1.
        Assert.Equal(abs.Challenges.Single(), OidcEndpoints.Challenge(abs.Verifiers.Single()));
    }

    [Fact]
    public async Task Callback_with_mismatched_state_does_not_call_abs()
    {
        var abs = new AbsStub();
        using var f = Factory(abs);
        using var c = NoRedirects(f);

        await c.GetAsync("/oidc/start");
        var res = await c.GetAsync("/oidc/callback?code=the-code&state=not-the-state");

        Assert.Equal("/login?error=sso", res.Headers.Location?.ToString());
        Assert.Single(abs.Requests); // leg 1 only — no exchange was attempted
    }

    [Fact]
    public async Task Callback_without_the_flow_cookie_redirects_to_login_error()
    {
        var abs = new AbsStub();
        using var f = Factory(abs);
        using var c = NoRedirects(f);

        var res = await c.GetAsync("/oidc/callback?code=the-code&state=whatever");

        Assert.Equal("/login?error=sso", res.Headers.Location?.ToString());
        Assert.Empty(abs.Requests);
    }

    [Fact]
    public async Task Callback_without_a_code_redirects_to_login_error()
    {
        var abs = new AbsStub();
        using var f = Factory(abs);
        using var c = NoRedirects(f);

        await c.GetAsync("/oidc/start");
        var res = await c.GetAsync($"/oidc/callback?state={abs.States.Single()}");

        Assert.Equal("/login?error=sso", res.Headers.Location?.ToString());
        Assert.Single(abs.Requests);
    }

    [Fact]
    public async Task Callback_on_abs_401_redirects_to_login_error_without_a_session()
    {
        var abs = new AbsStub { Leg3Status = HttpStatusCode.Unauthorized };
        using var f = Factory(abs);
        using var c = NoRedirects(f);

        await c.GetAsync("/oidc/start");
        var res = await c.GetAsync($"/oidc/callback?code=the-code&state={abs.States.Single()}");

        Assert.Equal("/login?error=sso", res.Headers.Location?.ToString());
        Assert.DoesNotContain(SetCookies(res), v => v.StartsWith("inkshelf_session="));
    }

    [Fact]
    public async Task Two_interleaved_flows_do_not_share_abs_cookies()
    {
        // Guards UseCookies = false on the shared handler: a CookieContainer there
        // would pool both users' ABS sessions and one flow would replay the other's.
        var abs = new AbsStub();
        using var f = Factory(abs);
        using var alice = NoRedirects(f);
        using var bob = NoRedirects(f);

        await alice.GetAsync("/oidc/start");
        await bob.GetAsync("/oidc/start");
        await alice.GetAsync($"/oidc/callback?code=a&state={abs.States[0]}");
        await bob.GetAsync($"/oidc/callback?code=b&state={abs.States[1]}");

        Assert.Equal(2, abs.ExchangeCookies.Count);
        Assert.Contains("connect.sid=s%3A1", abs.ExchangeCookies[0]);
        Assert.Contains("connect.sid=s%3A2", abs.ExchangeCookies[1]);
    }

    [Fact]
    public async Task Login_page_hides_the_sso_button_when_disabled()
    {
        using var f = Factory(enabled: false);
        var html = await f.CreateClient().GetStringAsync("/login");
        Assert.DoesNotContain("/oidc/start", html);
    }

    [Fact]
    public async Task Login_page_offers_sso_with_the_default_label_when_enabled()
    {
        using var f = Factory();
        var html = await f.CreateClient().GetStringAsync("/login");
        Assert.Contains("/oidc/start", html);
        Assert.Contains("Log in with SSO", html);
    }

    [Fact]
    public async Task Login_page_uses_the_configured_button_label()
    {
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ABS_URL", "http://abs.local");
            b.UseSetting("OIDC_ENABLED", "true");
            b.UseSetting("OIDC_BUTTON_LABEL", "Log in with Acme ID");
        });
        var html = await f.CreateClient().GetStringAsync("/login");
        Assert.Contains("Log in with Acme ID", html);
        Assert.DoesNotContain("Log in with SSO", html);
    }

    [Fact]
    public async Task Login_page_reports_a_failed_sso_attempt()
    {
        using var f = Factory();
        var html = await f.CreateClient().GetStringAsync("/login?error=sso");
        Assert.Contains("SSO login failed", html);
    }

    [Fact]
    public async Task Each_flow_gets_a_fresh_state_and_verifier()
    {
        var abs = new AbsStub();
        using var f = Factory(abs);
        using var c = NoRedirects(f);

        await c.GetAsync("/oidc/start");
        await c.GetAsync("/oidc/start");

        Assert.Equal(2, abs.States.Distinct().Count());
        Assert.Equal(2, abs.Challenges.Distinct().Count());
    }
}
