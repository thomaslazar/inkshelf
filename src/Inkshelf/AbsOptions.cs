namespace Inkshelf;

// Typed view of the app's configuration, bound once at startup so config reads
// live in one place instead of scattered Configuration["…"] lookups. Config keys:
// ABS_URL (required), CachePath, DataProtectionKeysPath.
public sealed class AbsOptions
{
    public string AbsUrl { get; set; } = "";
    public string? CachePath { get; set; }
    public string? DataProtectionKeysPath { get; set; }
}
