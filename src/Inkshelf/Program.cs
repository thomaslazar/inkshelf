using Inkshelf.Abs;
using Inkshelf.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

var absUrl = builder.Configuration["ABS_URL"];
if (string.IsNullOrWhiteSpace(absUrl))
    throw new InvalidOperationException("ABS_URL is required.");

var keysPath = builder.Configuration["DataProtectionKeysPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, ".keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("inkshelf")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<AbsSession>();
builder.Services.AddHttpClient<AbsClient>(c =>
{
    c.BaseAddress = new Uri(absUrl);
    // Identify the client. Some reverse proxies fronting ABS (e.g. Cosmos)
    // reject requests with no User-Agent with a 403 before they reach ABS.
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Inkshelf/1.0");
});
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AddPageRoute("/Library", "library/{id}");
});

var app = builder.Build();

var fho = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor };
// Sidecar sits behind the operator's own reverse proxy; the proxy isn't on a known
// network/loopback, so trust forwarded headers from it. Deploy on a trusted network only.
fho.KnownIPNetworks.Clear();
fho.KnownProxies.Clear();
app.UseForwardedHeaders(fho);

app.UseStaticFiles();

// Any page/handler that hits an unauthenticated/expired session throws
// AbsAuthException; a double-401 (retry also rejected) surfaces as a raw
// AbsUnauthorizedException instead. Either way, send the user to /login.
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) when (ex is AbsAuthException or AbsUnauthorizedException)
    {
        if (!ctx.Response.HasStarted) ctx.Response.Redirect("/login");
    }
});

app.MapRazorPages();

app.MapGet("/cover/{id}", async (string id, int? w, AbsSession session, AbsClient client, CancellationToken ct) =>
{
    var width = w is > 0 and <= 400 ? w.Value : 120;
    var (stream, contentType) = await session.ExecuteAsync(
        (tok, c) => client.GetCoverAsync(tok, id, width, c), ct);
    return Results.Stream(stream, contentType);
});

app.MapPost("/logout", async (HttpContext httpContext, IAntiforgery antiforgery, TokenStore store) =>
{
    try
    {
        await antiforgery.ValidateRequestAsync(httpContext);
    }
    catch (AntiforgeryValidationException)
    {
        return Results.BadRequest();
    }

    store.Clear();
    return Results.Redirect("/login");
});

app.Run();

public partial class Program { }
