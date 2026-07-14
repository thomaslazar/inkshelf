using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Endpoints;
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
builder.Services.AddSingleton(new Inkshelf.Convert.EpubCache(cachePath));
builder.Services.AddSingleton<Inkshelf.Convert.EpubConverter>();
builder.Services.AddScoped<Inkshelf.Convert.ConvertService>();
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

app.MapCoverEndpoints();
app.MapDownloadEndpoints();
app.MapConvertEndpoints();

app.MapSessionEndpoints();

app.MapDiagEndpoints();

app.Run();

public partial class Program { }
