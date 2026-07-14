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

app.MapGet("/convert/{id}", async (string id, string? fresh, string? warm, HttpContext httpContext, AbsSession session, AbsClient client,
    Inkshelf.Convert.EpubCache cache, Inkshelf.Convert.EpubConverter converter, CancellationToken ct) =>
{
    Inkshelf.Abs.AbsItemDetail detail;
    try { detail = await session.ExecuteAsync((tok, c) => client.GetItemDetailAsync(tok, id, c), ct); }
    catch (HttpRequestException) { return Results.NotFound(); }

    var ef = detail.Media?.EbookFile;
    var fmt = ef?.EbookFormat;
    if (ef?.Metadata is null || (fmt != "cbz" && fmt != "cbr")) return Results.NotFound();

    var size = ef.Metadata.Size; var mtime = ef.Metadata.MtimeMs;
    if (fresh is "1" or "true") cache.RemoveForItem(id);

    // Page-image cap + DPR from the device's screen (the layout script reports
    // "cssW x cssH x dpr" in the "scr" cookie). No cookie (JS off) → 0×0 → no
    // downscaling and viewport = image size.
    var (maxW, maxH, dpr) = Inkshelf.ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"]);

    // authorName isn't always populated on uploaded ebooks; fall back to the
    // authors[] list. Used for both the embedded metadata and the file name.
    var md = detail.Media!.Metadata!;
    var title = md.Title ?? "Untitled";
    var author = md.AuthorName is { Length: > 0 } an ? an
        : (md.Authors is { Count: > 0 } ? md.Authors[0].Name : "Unknown");
    var seq = md.Series is { Count: > 0 } ? md.Series[0].Sequence : null;
    var seriesName = md.Series is { Count: > 0 } ? md.Series[0].Name : md.SeriesName;

    var path = cache.PathFor(id, size, mtime, maxW, maxH);
    if (!File.Exists(path))
    {
        app.Logger.LogInformation("Converting {Id} ({Fmt}, {Bytes} bytes, cap {W}x{H} @dpr {Dpr}) to EPUB…", id, fmt, size, maxW, maxH, dpr);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (archive, _) = await session.ExecuteAsync((tok, c) => client.GetEbookStreamAsync(tok, id, c), ct);
        using var buffered = new MemoryStream();
        await using (archive) await archive.CopyToAsync(buffered, ct);   // SharpCompress needs a seekable stream
        buffered.Position = 0;
        await converter.ConvertAsync(buffered, new Inkshelf.Convert.EbookMeta(title, author, seriesName, seq, id), path, maxW, maxH, dpr, ct);
        app.Logger.LogInformation("Converted {Id} in {Ms} ms → {OutBytes} bytes", id, sw.ElapsedMilliseconds, new FileInfo(path).Length);
    }
    else
    {
        app.Logger.LogInformation("Serving cached EPUB for {Id} ({OutBytes} bytes)", id, new FileInfo(path).Length);
    }

    // warm=1 (the listing's XHR) just ensures the EPUB is built + cached, so the
    // user's next tap downloads it instantly; it returns OK, not the file.
    if (warm is "1") return Results.Text("ok");

    var fileName = Sanitize($"{author} - {title}") + ".epub";
    return Results.File(path, "application/epub+zip", fileDownloadName: fileName);

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
});

app.MapSessionEndpoints();

app.MapDiagEndpoints();

app.Run();

public partial class Program { }
