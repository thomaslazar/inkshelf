using Inkshelf.Convert;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using System.Globalization;
using System.Security.Cryptography;

namespace Inkshelf.Auth;

// Per-device rendering preferences plus the favorite library, stored in one
// server-written cookie via static Read/Set. Distinct from the JS-written "scr"
// device probe — this is user CHOICE, scr is device TRUTH; the two are read
// together where conversion happens.
public sealed record DeviceSettings(bool Retina, bool Grayscale, string Lang)
{
    public const string Cookie = "inkshelf_settings";
    // Retina defaults ON — most readers want crisp pages; opt out per device.
    // Lang "" = no explicit choice yet (resolved from Accept-Language at render).
    public static readonly DeviceSettings Default = new(true, false, "");

    public const string LegacyFavCookie = "inkshelf_fav_library";

    // An init property rather than a fourth positional parameter, so the ten
    // existing `new DeviceSettings(a, b, c)` sites in the tests keep compiling —
    // those tests are the regression net for this refactor. Record equality still
    // covers it and `with { Fav = ... }` still works.
    public string Fav { get; init; } = "";

    // How two-page spreads (landscape page images) are rendered. An init property
    // for the same reason as Fav below. Defaults to Split: letterboxing a spread
    // wastes half the screen, and most readers would rather have two pages.
    public SpreadMode Spread { get; init; } = SpreadMode.SplitLeftFirst;

    // Page scale in PERCENT (100 = pages fill the screen). The manual fix for a reader
    // that cuts a strip off the page: at 95% the page lays out 5% smaller and the cut
    // falls outside it. Device-specific and unknowable from here, hence a knob.
    public int Scale { get; init; } = 100;

    // Lowest page scale accepted. The useful values turned out to be a percent or two
    // below 100 — a fixed list of coarse steps could not express them — so this is a
    // free number with a floor rather than a menu.
    public const int MinScale = 50;

    // A hand-entered screen geometry, used INSTEAD of the "scr" probe when
    // OverrideScreen is set. The numbers are kept even while the override is off,
    // so switching it off does not throw them away and the fields can show what
    // was last used. 0 means "nothing stored", which is what renders the field
    // blank rather than a misleading 0.
    public bool OverrideScreen { get; init; }
    public int OverrideW { get; init; }
    public int OverrideH { get; init; }
    public double OverrideDpr { get; init; }

    // The override as the converter wants it, or null when it is off or
    // incomplete. Incomplete counts as off: a half-filled override would produce
    // a zero-sized page, which is worse than falling back to the probe.
    public ScreenOverride? ActiveOverride =>
        OverrideScreen && OverrideW > 0 && OverrideH > 0 && OverrideDpr > 0
            ? new ScreenOverride(OverrideW, OverrideH, OverrideDpr)
            : null;

    // An opaque per-device handle, minted by Set (below) and used to key this
    // device's downloaded-file marks. An init property for the same reason as Fav:
    // the existing three-argument construction sites keep compiling.
    //
    // NOT a secret and NOT derived from anything the browser exposes — we mint it,
    // so no fingerprinting is involved and no privacy countermeasure applies to it.
    public string Did { get; init; } = "";

    // Keyed, NOT positional: "retina=1&gray=0&lang=de&fav=". Looks like a query
    // string because it is parsed by QueryHelpers, but it is a cookie value —
    // Response.Cookies.Append escapes the & and = to %26/%3D and the request side
    // unescapes them. Every key is always written, including empty ones: Read
    // distinguishes "key present but empty" from "key absent" and they mean
    // different things for fav (see Read).
    public string Serialize() =>
        $"retina={(Retina ? 1 : 0)}&gray={(Grayscale ? 1 : 0)}"
        + $"&lang={SanitizeLang(Lang)}&fav={SanitizeId(Fav)}&did={SanitizeId(Did)}"
        + $"&spread={Spread.ToString().ToLowerInvariant()}&scale={Scale}"
        + $"&ovr={(OverrideScreen ? 1 : 0)}&ovrw={SanitizeDim(OverrideW)}&ovrh={SanitizeDim(OverrideH)}"
        + $"&ovrd={SanitizeDpr(OverrideDpr).ToString(CultureInfo.InvariantCulture)}";

    public static DeviceSettings Read(HttpRequest req)
    {
        if (!req.Cookies.TryGetValue(Cookie, out var v) || string.IsNullOrEmpty(v))
            return Default with { Fav = LegacyFav(req) };

        // No '=' means the legacy positional shape ("10", "10de"). Written before
        // the keyed format; parsed here so existing devices keep their settings.
        if (!v.Contains('=')) return ReadLegacy(v) with { Fav = LegacyFav(req) };

        var q = QueryHelpers.ParseQuery(v);
        return new DeviceSettings(
            Flag(q, "retina", Default.Retina),
            Flag(q, "gray", Default.Grayscale),
            q.TryGetValue("lang", out var lang) ? SanitizeLang(lang.ToString()) : Default.Lang)
        {
            // PRESENCE, not emptiness. `fav=` present-but-empty means deliberately
            // un-favorited; falling back to the legacy cookie on empty would
            // resurrect a favorite the user just cleared.
            Fav = q.TryGetValue("fav", out var fav) ? SanitizeId(fav.ToString()) : LegacyFav(req),
            Did = q.TryGetValue("did", out var did) ? SanitizeId(did.ToString()) : "",
            // Absent (a cookie written before these settings existed) or unparseable →
            // the documented default, NOT the enum's zero value / a zero scale.
            Spread = q.TryGetValue("spread", out var sp) && Enum.TryParse<SpreadMode>(sp.ToString(), true, out var sm)
                ? sm : Default.Spread,
            Scale = q.TryGetValue("scale", out var sc) && int.TryParse(sc.ToString(), out var pc)
                ? SanitizeScale(pc) : Default.Scale,
            OverrideScreen = Flag(q, "ovr", Default.OverrideScreen),
            OverrideW = q.TryGetValue("ovrw", out var ow) && int.TryParse(ow.ToString(), out var owv)
                ? SanitizeDim(owv) : 0,
            OverrideH = q.TryGetValue("ovrh", out var oh) && int.TryParse(oh.ToString(), out var ohv)
                ? SanitizeDim(ohv) : 0,
            OverrideDpr = q.TryGetValue("ovrd", out var od) ? SanitizeDpr(ParseDpr(od.ToString())) : 0,
        };
    }

    // An absent key means "not specified", which must land on the DOCUMENTED
    // default — retina defaults ON, so a plain `== "1"` would silently flip it off.
    // ParseQuery hands back a plain Dictionary whose indexer THROWS on a missing
    // key, so every lookup goes through TryGetValue.
    private static bool Flag(Dictionary<string, StringValues> q, string key, bool fallback) =>
        q.TryGetValue(key, out var v) && v.Count > 0 ? v[0] == "1" : fallback;

    // Two 0/1 flags then an optional language code, e.g. "10de". Anything
    // malformed → Default.
    private static DeviceSettings ReadLegacy(string v) =>
        v is { Length: >= 2 } && v[0] is '0' or '1' && v[1] is '0' or '1'
            ? new DeviceSettings(v[0] == '1', v[1] == '1', SanitizeLang(v.Length > 2 ? v[2..] : ""))
            : Default;

    // A hand-edited cookie must not mint an absurd page size. Out of range means the
    // documented default, not a clamp: 20 is not a request for 50.
    public static int SanitizeScale(int pct) => pct >= MinScale && pct <= 100 ? pct : Default.Scale;

    // Out of range becomes 0 ("nothing stored") rather than being clamped to the
    // bound: a typo'd 99999 is not a request for 4096, it is a mistake, and
    // silently converting at a size the user never asked for is worse than
    // falling back to the probe.
    // NOT `Convert.ScreenTarget…` — `Convert` binds to System.Convert here, which is
    // why this file already fully-qualifies System.Convert.ToHexString. The file's
    // `using Inkshelf.Convert;` makes the bare type name work.
    public static int SanitizeDim(int px) => px > 0 && px <= ScreenTarget.MaxDimension ? px : 0;

    // Lower bound is 1, not 0: EpubWriter.WriteAsync documents "pxPerCss >= 1", and
    // dpr is the only input that can violate it. A dpr below 1 would enlarge the
    // declared viewport past the physical screen — pages clipped to a corner, the
    // exact disease this override cures.
    public static double SanitizeDpr(double dpr) => dpr >= 1 && dpr <= ScreenTarget.MaxDpr ? dpr : 0;

    // Accepts both "1.875" and "1,875": the UI is translated and a comma is what a
    // German-locale user will type. 0 on anything unparseable.
    public static double ParseDpr(string? s) =>
        !string.IsNullOrWhiteSpace(s)
        && double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d : 0;

    // Accept a short lowercase code (letters + dash), else "" (→ resolve from header).
    private static string SanitizeLang(string s)
    {
        if (s.Length is 0 or > 8) return "";
        foreach (var c in s)
            if (c is not ((>= 'a' and <= 'z') or '-')) return "";
        return s;
    }

    private static string LegacyFav(HttpRequest req) =>
        req.Cookies.TryGetValue(LegacyFavCookie, out var v) ? SanitizeId(v) : "";

    // An opaque ABS library id, so allow only what those ids use. This is a trust
    // boundary, not tidiness: `libraryId` arrives from a form POST, and a value
    // containing '&' would inject extra keys into the cookie we write. Rejecting
    // '%' also rules out double-decoding, since ParseQuery URL-decodes a value the
    // cookie layer already unescaped once.
    private static string SanitizeId(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64) return "";
        foreach (var c in s)
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-'))
                return "";
        return s;
    }

    // Returns the settings as written, including any id minted here — the download
    // endpoints need it to record a mark for a device seen for the first time.
    // Minting lives in Set so that no call site can write this cookie without an
    // id; every write path (POST /settings, POST /favorite, Index's stale-favorite
    // clear) therefore establishes one.
    public static DeviceSettings Set(HttpResponse res, DeviceSettings settings)
    {
        if (string.IsNullOrEmpty(settings.Did)) settings = settings with { Did = NewDid() };
        var forceSecure = res.HttpContext.RequestServices?.GetService<AbsOptions>()?.ForceSecureCookies ?? false;
        res.Cookies.Append(Cookie, settings.Serialize(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = forceSecure || res.HttpContext.Request.IsHttps,
            IsEssential = true,
            Path = "/",
            MaxAge = TimeSpan.FromDays(365)
        });
        // The favorite now lives in the settings cookie. Drop the old one so it
        // can't linger and shadow a later un-favorite.
        res.Cookies.Delete(LegacyFavCookie, new CookieOptions { Path = "/" });
        return settings;
    }

    // 16 hex chars from a crypto RNG: unique enough for a household, and inside
    // SanitizeId's allowlist so it survives its own round trip.
    private static string NewDid() => System.Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    // Public so DownloadMarks can gate a cookie-supplied id before it becomes a
    // file name. Reuses the one allowlist rather than restating it.
    public static bool IsValidDid(string? did) => !string.IsNullOrEmpty(did) && SanitizeId(did) == did;
}
