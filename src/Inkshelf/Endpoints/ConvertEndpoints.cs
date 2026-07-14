using Inkshelf.Convert;

namespace Inkshelf.Endpoints;

public static class ConvertEndpoints
{
    public static void MapConvertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/convert/{id}", async (string id, string? fresh, string? warm,
            HttpContext httpContext, ConvertService convert, CancellationToken ct) =>
        {
            // Page-image cap + DPR from the device's screen (the layout script reports
            // "cssW x cssH x dpr" in the "scr" cookie). No cookie (JS off) → 0×0 → no
            // downscaling and viewport = image size.
            var (maxW, maxH, dpr) = Inkshelf.ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"]);

            var outcome = await convert.ConvertAsync(
                id, fresh is "1" or "true", warm is "1", maxW, maxH, dpr, ct);

            return outcome.Kind switch
            {
                ConvertResultKind.NotFound => Results.NotFound(),
                ConvertResultKind.Warmed => Results.Text("ok"),
                _ => Results.File(outcome.FilePath!, "application/epub+zip",
                        fileDownloadName: outcome.DownloadName)
            };
        });
    }
}
