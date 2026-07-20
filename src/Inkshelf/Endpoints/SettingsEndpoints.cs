using Inkshelf.Auth;
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
            // Unchecked checkboxes send no field → absent == off.
            var settings = new DeviceSettings(form.ContainsKey("retina"), form.ContainsKey("grayscale"));
            DeviceSettings.Set(ctx.Response, settings);
            return Results.Redirect("/settings"); // PRG: back to the page, showing saved state
        }).DisableAntiforgery();
    }
}
