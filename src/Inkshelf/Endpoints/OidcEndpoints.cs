using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Inkshelf.Abs;
using Inkshelf.Auth;

namespace Inkshelf.Endpoints;

// Login through the OIDC provider ABS is configured with.
//
// ABS's own web callback flow is unusable from here — it demands a callback URL
// on ABS's origin — so this drives the "mobile" flow ABS offers third-party
// clients. The twist is that leg 1 runs server-side: ABS's token exchange needs
// the session and auth_method cookies from that leg, and the browser cannot pass
// cookies set on ABS's origin to us.
public static class OidcEndpoints
{
    public static void MapOidcEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<AbsOptions>();
        if (!options.OidcEnabled) return; // not mapped at all when disabled

        app.MapGet("/oidc/start", async (HttpContext ctx, AbsAuthClient auth, OidcFlowStore flows,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var redirectUri = CallbackUri(ctx, options);
            var verifier = RandomToken(32);
            var state = RandomToken(16);
            try
            {
                var (authorizeUrl, cookies) =
                    await auth.StartOidcAsync(redirectUri, Challenge(verifier), state, ct);
                flows.Save(new OidcFlow(state, verifier, cookies));
                return Results.Redirect(authorizeUrl);
            }
            catch (Exception ex) when (ex is AbsOidcException or HttpRequestException
                or InvalidOperationException)
            {
                // An unwhitelisted callback URL is the likely cause, so log the
                // URL we sent — that line is the operator's fix.
                logs.CreateLogger(typeof(OidcEndpoints)).LogWarning(ex,
                    "OIDC start failed. Is {RedirectUri} in the ABS mobile redirect URIs?",
                    redirectUri);
                return Results.Redirect("/login?error=sso");
            }
        });

        app.MapGet("/oidc/callback", async (HttpContext ctx, string? code, string? state,
            AbsAuthClient auth, OidcFlowStore flows, TokenStore tokens,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var log = logs.CreateLogger(typeof(OidcEndpoints));
            var flow = flows.Read();
            if (flow is null)
            {
                log.LogWarning("OIDC callback without a flow cookie (expired, or not started here).");
                return Results.Redirect("/login?error=sso");
            }
            if (string.IsNullOrEmpty(code) || !FixedTimeEquals(state, flow.State))
            {
                log.LogWarning("OIDC callback with a missing code or mismatched state.");
                return Results.Redirect("/login?error=sso");
            }

            try
            {
                tokens.Save(await auth.CompleteOidcAsync(code, flow.State, flow.Verifier,
                    flow.Cookies, ct));
                flows.Clear();
                return Results.Redirect("/");
            }
            catch (Exception ex) when (ex is AbsOidcException or HttpRequestException
                or InvalidOperationException)
            {
                log.LogWarning(ex, "OIDC token exchange failed.");
                return Results.Redirect("/login?error=sso");
            }
        });
    }

    // Must match the whitelist entry in ABS exactly. Scheme follows
    // FORCE_SECURE_COOKIES, the knob a TLS-terminating-proxy deployment already
    // sets, rather than adding a second one that can disagree with it.
    private static string CallbackUri(HttpContext ctx, AbsOptions options)
    {
        var scheme = options.ForceSecureCookies || ctx.Request.IsHttps ? "https" : "http";
        return $"{scheme}://{ctx.Request.Host}/oidc/callback";
    }

    private static string RandomToken(int bytes) =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    internal static string Challenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static bool FixedTimeEquals(string? a, string b) =>
        a is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
