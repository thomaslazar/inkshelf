using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

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

var cachePath = builder.Configuration["CachePath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, ".cache", "epub");
Directory.CreateDirectory(cachePath);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TokenStore>();
builder.Services.AddScoped<AbsSession>();
var absUserAgent = $"Inkshelf/{typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0"}";
builder.Services.AddHttpClient<AbsClient>(c =>
{
    c.BaseAddress = new Uri(absUrl);
    // Identify the client: some reverse proxies / WAFs in front of ABS reject
    // requests with no User-Agent (HTTP 403) before they reach the server.
    c.DefaultRequestHeaders.UserAgent.ParseAdd(absUserAgent);
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
    try
    {
        var (stream, contentType) = await session.ExecuteAsync(
            (tok, c) => client.GetCoverAsync(tok, id, width, c), ct);
        return Results.Stream(stream, contentType);
    }
    catch (HttpRequestException)
    {
        // Item has no cover (ABS 404) or a transient fetch error — the <img>
        // just shows nothing rather than the page 500ing.
        return Results.NotFound();
    }
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

app.MapPost("/favorite", async (HttpContext ctx, IAntiforgery antiforgery, [FromForm] string libraryId) =>
{
    try { await antiforgery.ValidateRequestAsync(ctx); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    if (Favorites.Read(ctx.Request) == libraryId) Favorites.Clear(ctx.Response);
    else Favorites.Set(ctx.Response, libraryId);
    return Results.Redirect($"/library/{libraryId}");
}).DisableAntiforgery();

// Receives the /diag.html browser capability probe and logs it, so device
// limitations can be collected without a screenshot. No auth (pre-login tool).
app.MapPost("/diag", async (HttpContext ctx) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var body = await reader.ReadToEndAsync();
    app.Logger.LogInformation("Browser probe: {Probe}", body);
    return Results.Ok();
});

app.Run();

public partial class Program { }
