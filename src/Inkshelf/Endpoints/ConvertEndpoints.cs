using Inkshelf.Convert;

namespace Inkshelf.Endpoints;

public static class ConvertEndpoints
{
    public static void MapConvertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/convert/{id}", async (string id, string? fresh, string? warm,
            string? status, string? @return, HttpContext httpContext, ConvertService convert, CancellationToken ct) =>
        {
            var t = ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"]);

            // JS poll: report status, no enqueue.
            if (status is "1")
            {
                var s = await convert.StatusAsync(id, t, ct);
                return s.Status == ConvertStatus.None
                    ? Results.NotFound()
                    : Results.Text(Text(s.Status));
            }

            var result = await convert.KickAsync(id, fresh is "1" or "true", t, ct);
            if (result.Status == ConvertStatus.None) return Results.NotFound();

            // JS kick (warm): return the status as text; 202 while not yet done.
            if (warm is "1")
                return result.Status == ConvertStatus.Done
                    ? Results.Text("done")
                    : Results.Text(Text(result.Status), statusCode: StatusCodes.Status202Accepted);

            // Plain navigation (no JS): download when ready, else back to the listing.
            return result.Status == ConvertStatus.Done
                ? Results.File(result.FilePath!, "application/epub+zip", fileDownloadName: result.DownloadName)
                : Results.Redirect(LocalReturn(@return));
        });
    }

    private static string Text(ConvertStatus s) => s.ToString().ToLowerInvariant();

    // Open-redirect guard: only same-site absolute paths are honored.
    private static string LocalReturn(string? r) =>
        !string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//") && !r.Contains('\\') ? r : "/";
}
