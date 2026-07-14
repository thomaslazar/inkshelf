namespace Inkshelf;

// Typed view of the app's configuration, bound once at startup so config reads
// live in one place instead of scattered Configuration["…"] lookups. Config keys:
// ABS_URL (required), CachePath, DataProtectionKeysPath, DIAG_ENABLED, FORCE_SECURE_COOKIES, TRUSTED_PROXY.
public sealed class AbsOptions
{
    public string AbsUrl { get; set; } = "";
    public string? CachePath { get; set; }
    public string? DataProtectionKeysPath { get; set; }
    // Force the Secure flag on cookies even when Request.IsHttps is false (the app
    // sits behind a TLS-terminating proxy). Defaults false = derive from IsHttps.
    public bool ForceSecureCookies { get; set; }
    // Comma-separated IPs/CIDRs allowed to set forwarded headers. Null = trust all
    // (deploy behind a trusted proxy). Consumed in Program.cs forwarded-headers setup.
    public string? TrustedProxy { get; set; }
    // Whether the unauthenticated /diag probe endpoint is mapped. Default true.
    public bool DiagEnabled { get; set; } = true;
    // Soft cap on total EPUB cache bytes; oldest entries are evicted past it. Default 1 GiB.
    public long MaxCacheBytes { get; set; } = 1_073_741_824;
    // Max bytes buffered from an ebook archive before conversion; larger archives
    // are refused (decompression-bomb / OOM guard). Default 500 MiB.
    public long MaxArchiveBytes { get; set; } = 524_288_000;
}
