using Inkshelf;
using Inkshelf.Abs;
using Inkshelf.Auth;
using Inkshelf.Convert;
using Inkshelf.Endpoints;
using Inkshelf.Localization;
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
    LocalesPath = builder.Configuration["LOCALES_PATH"],
    DiagEnabled = !string.Equals(builder.Configuration["DIAG_ENABLED"], "false", StringComparison.OrdinalIgnoreCase),
    ForceSecureCookies = bool.TryParse(builder.Configuration["FORCE_SECURE_COOKIES"], out var fsc) && fsc,
    TrustedProxy = builder.Configuration["TRUSTED_PROXY"],
    MaxCacheBytes = long.TryParse(builder.Configuration["MaxCacheBytes"], out var mcb) && mcb > 0 ? mcb : 1_073_741_824,
    MaxArchiveBytes = long.TryParse(builder.Configuration["MaxArchiveBytes"], out var mab) && mab > 0 ? mab : 524_288_000,
    MaxConcurrentConversions = int.TryParse(builder.Configuration["MaxConcurrentConversions"], out var mcc) && mcc > 0 ? mcc : 1,
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
// Handler-FREE (no AbsAuthHandler) — the worker supplies the bearer; ConfigureAbs
// gives it the BaseAddress + required User-Agent. See AbsDownloadClient.
builder.Services.AddHttpClient<AbsDownloadClient>(ConfigureAbs);
builder.Services.AddSingleton(new EpubCache(cachePath));
builder.Services.AddSingleton<EpubConverter>();
builder.Services.AddSingleton<ConvertLock>();
builder.Services.AddSingleton<ConvertQueue>();
builder.Services.AddHostedService<ConvertWorker>();
builder.Services.AddScoped<ConvertService>();
// UI localisation: load <lang>.json once at startup; Localizer resolves the
// per-request language and is injected into every view.
var localesPath = absOptions.LocalesPath
    ?? Path.Combine(builder.Environment.ContentRootPath, "locales");
builder.Services.AddSingleton(sp =>
    LocalizationCatalog.Load(localesPath, sp.GetService<ILoggerFactory>()?.CreateLogger("Localization")));
builder.Services.AddSingleton<Localizer>();
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AddPageRoute("/Library", "library/{id}");
});

var app = builder.Build();

var fho = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor };
fho.KnownIPNetworks.Clear();
fho.KnownProxies.Clear();
var (trustedProxies, trustedNetworks) = ForwardedProxies.Parse(absOptions.TrustedProxy);
// With TRUSTED_PROXY set, only those proxies/networks may set forwarded headers
// (default-deny). With it unset, both lists stay empty → forwarded headers are
// trusted from any hop (deploy behind a trusted proxy; FORCE_SECURE_COOKIES
// protects the cookie Secure flag independently).
foreach (var p in trustedProxies) fho.KnownProxies.Add(p);
foreach (var net in trustedNetworks) fho.KnownIPNetworks.Add(net);
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
app.MapSettingsEndpoints();
app.MapReadEndpoints();

if (absOptions.DiagEnabled) app.MapDiagEndpoints();

app.Run();

public partial class Program { }
