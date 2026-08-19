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
            var settings = DeviceSettings.Read(ctx.Request) with
            {
                Retina = form.ContainsKey("retina"),
                Grayscale = form.ContainsKey("grayscale"),
                Lang = form["lang"].ToString(),
                Spread = Enum.TryParse<SpreadMode>(form["spread"].ToString(), true, out var sp)
                    ? sp : DeviceSettings.Default.Spread,
                Scale = int.TryParse(form["scale"].ToString(), out var pc)
                    ? DeviceSettings.SanitizeScale(pc) : DeviceSettings.Default.Scale,
            };
            DeviceSettings.Set(ctx.Response, settings);
            return Results.Redirect("/settings"); // PRG: back to the page, showing saved state
        }).DisableAntiforgery();
    }
}
