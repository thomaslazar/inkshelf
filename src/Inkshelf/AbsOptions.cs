namespace Inkshelf;

// Typed view of the app's configuration, bound once at startup so config reads
// live in one place instead of scattered Configuration["…"] lookups. Config keys:
// ABS_URL (required), CachePath, DataProtectionKeysPath, DIAG_ENABLED, FORCE_SECURE_COOKIES, LOCALES_PATH, LOCALES_OVERRIDE_PATH, TRUSTED_PROXY.
public sealed class AbsOptions
{
    public string AbsUrl { get; set; } = "";
    public string? CachePath { get; set; }
    public string? DataProtectionKeysPath { get; set; }
    // Baseline directory of <lang>.json UI translation files, scanned at startup.
    // Null → "<ContentRoot>/locales" (the shipped set). Don't mount over this.
    public string? LocalesPath { get; set; }
    // Optional extra directory merged on top of LocalesPath (its keys/files win),
    // so mounting custom or extra translations here leaves the shipped baseline
    // intact. Null → none. This is the safe path to bind-mount.
    public string? LocalesOverridePath { get; set; }
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
    // Max conversions the background worker runs at once. Default 1 — a small
    // host must not run two ImageSharp resizes concurrently (CPU/RAM thrash).
    public int MaxConcurrentConversions { get; set; } = 1;
}
