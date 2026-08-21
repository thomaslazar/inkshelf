using Inkshelf.Auth;
using Inkshelf.Convert;
using Microsoft.AspNetCore.Antiforgery;

namespace Inkshelf.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/settings", async (HttpContext ctx, IAntiforgery antiforgery) =>
        {
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }

            var form = await ctx.Request.ReadFormAsync();
            // Unchecked checkboxes send no field → absent == off. lang comes from
            // the <select>; DeviceSettings sanitises it on both write (Serialize)
            // and read.
            // `with`, NOT a fresh instance — the favorite lives in this same cookie
            // and constructing a new record would wipe it.
            var stored = DeviceSettings.Read(ctx.Request);
            var overriding = form.ContainsKey("ovr");
            var settings = stored with
            {
                // The retina box is disabled while an override is on, and a disabled
                // input is not submitted — indistinguishable from unchecked. The
                // hidden companion is disabled by the same script line, so its
                // PRESENCE means the box was live and its value can be trusted;
                // absence means it was disabled and the stored value stands.
                Retina = form.ContainsKey("retinalive") ? form.ContainsKey("retina") : stored.Retina,
                Grayscale = form.ContainsKey("grayscale"),
                Lang = form["lang"].ToString(),
                Spread = Enum.TryParse<SpreadMode>(form["spread"].ToString(), true, out var sp)
                    ? sp : DeviceSettings.Default.Spread,
                Scale = int.TryParse(form["scale"].ToString(), out var pc)
                    ? DeviceSettings.SanitizeScale(pc) : DeviceSettings.Default.Scale,
                OverrideScreen = overriding,
                // Same trap, other direction: the three numbers are disabled while
                // the override is off, so keep what is stored rather than zeroing
                // values the user had to look up.
                OverrideW = form.ContainsKey("ovrw")
                    ? DeviceSettings.SanitizeDim(int.TryParse(form["ovrw"].ToString(), out var ow) ? ow : 0)
                    : stored.OverrideW,
                OverrideH = form.ContainsKey("ovrh")
                    ? DeviceSettings.SanitizeDim(int.TryParse(form["ovrh"].ToString(), out var oh) ? oh : 0)
                    : stored.OverrideH,
                OverrideDpr = form.ContainsKey("ovrd")
                    ? DeviceSettings.SanitizeDpr(DeviceSettings.ParseDpr(form["ovrd"].ToString()))
                    : stored.OverrideDpr,
            };
            DeviceSettings.Set(ctx.Response, settings);

            // Ticked but unusable — a value out of range is dropped to 0, and blanks
            // are 0 already, so the override is stored yet inactive and conversion
            // quietly keeps using the probe. Say so: without this the field simply
            // re-displays the detected number and the setting looks broken.
            var unusable = settings.OverrideScreen && settings.ActiveOverride is null;

            // The page scale silently reverts the same way: an out-of-range number
            // becomes the default, which looks like the field ignoring you.
            var rawScale = form["scale"].ToString();
            var scaleRejected = !string.IsNullOrWhiteSpace(rawScale)
                && (!int.TryParse(rawScale, out var typed) || DeviceSettings.SanitizeScale(typed) != typed);

            // PRG: back to the page, showing saved state.
            var flags = (unusable ? "range=1" : "") + (unusable && scaleRejected ? "&" : "")
                + (scaleRejected ? "scalerange=1" : "");
            return Results.Redirect(flags.Length == 0 ? "/settings" : $"/settings?{flags}");
        }).DisableAntiforgery();
    }
}
