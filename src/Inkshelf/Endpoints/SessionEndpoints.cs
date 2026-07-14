using Inkshelf.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Inkshelf.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/logout", async (HttpContext httpContext, IAntiforgery antiforgery, TokenStore store) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(httpContext);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            store.Clear();
            return Results.Redirect("/login");
        }).DisableAntiforgery();

        app.MapPost("/favorite", async (HttpContext ctx, IAntiforgery antiforgery, [FromForm] string libraryId) =>
        {
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            if (Favorites.Read(ctx.Request) == libraryId) Favorites.Clear(ctx.Response);
            else Favorites.Set(ctx.Response, libraryId);
            return Results.Redirect($"/library/{libraryId}");
        }).DisableAntiforgery();
    }
}
