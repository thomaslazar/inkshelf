using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Inkshelf.Endpoints;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

var absOptions = new AbsOptions
{
    AbsUrl = builder.Configuration["ABS_URL"] ?? "",
    CachePath = builder.Configuration["CachePath"],
    DataProtectionKeysPath = builder.Configuration["DataProtectionKeysPath"],
};
// Fail fast on missing required config. SmokeTests.MissingAbsUrl_FailsStartup
// depends on this exact exception type.
if (string.IsNullOrWhiteSpace(absOptions.AbsUrl))
    throw new InvalidOperationException("ABS_URL is required.");
builder.Services.AddSingleton(absOptions);

var keysPath = absOptions.DataProtectionKeysPath
    ?? Path.Combine(builder.Environment.ContentRootPath, ".keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .SetApplicationName("inkshelf")
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

var cachePath = absOptions.CachePath
    ?? Path.Combine(builder.Environment.ContentRootPath, ".cache", "epub");
Directory.CreateDirectory(cachePath);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<TokenStore>();
builder.Services.AddTransient<AbsAuthHandler>();
var absUserAgent = $"Inkshelf/{typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0"}";
void ConfigureAbs(HttpClient c)
{
    c.BaseAddress = new Uri(absOptions.AbsUrl);
    // Identify the client: some reverse proxies / WAFs in front of ABS reject
    // requests with no User-Agent (HTTP 403) before they reach the server.
    c.DefaultRequestHeaders.UserAgent.ParseAdd(absUserAgent);
}
builder.Services.AddHttpClient<AbsAuthClient>(ConfigureAbs);
builder.Services.AddHttpClient<AbsApiClient>(ConfigureAbs).AddHttpMessageHandler<AbsAuthHandler>();
builder.Services.AddSingleton(new EpubCache(cachePath));
builder.Services.AddSingleton<EpubConverter>();
builder.Services.AddScoped<ConvertService>();
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
