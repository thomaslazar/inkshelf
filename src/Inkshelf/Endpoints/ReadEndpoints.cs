using Inkshelf.Abs;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace Inkshelf.Endpoints;

public static class ReadEndpoints
{
    public static void MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/read/{id}", async (string id, HttpContext ctx, IAntiforgery antiforgery,
            AbsApiClient api, [FromForm] string? read, [FromForm(Name = "return")] string? @return,
            string? xhr, CancellationToken ct) =>
        {
            try { await antiforgery.ValidateRequestAsync(ctx); }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }

            await api.SetReadAsync(id, read == "1", ct);
            // The script updates the button itself and has no use for a listing it
            // would only throw away, so answer with nothing. Signalled by a query
            // parameter rather than a header to match how convert already says what
            // it wants (?warm=1, ?status=1).
            return xhr == "1" ? Results.NoContent() : Results.Redirect(LocalReturn(@return));
        }).DisableAntiforgery();
    }

    // Open-redirect guard: only same-site absolute paths are honored.
    private static string LocalReturn(string? r) =>
        !string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//") && !r.Contains('\\') ? r : "/";
}
