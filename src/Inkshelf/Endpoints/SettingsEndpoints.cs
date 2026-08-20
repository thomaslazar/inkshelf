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
                // A DISABLED input is not submitted, and the page disables retina
                // while the override is on. So "absent" only means "off" here when
                // the override is off — otherwise saving the override would quietly
                // switch retina off.
                Retina = overriding ? stored.Retina : form.ContainsKey("retina"),
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
            return Results.Redirect("/settings"); // PRG: back to the page, showing saved state
        }).DisableAntiforgery();
    }
}
