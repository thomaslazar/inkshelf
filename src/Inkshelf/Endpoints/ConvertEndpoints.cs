using Inkshelf.Auth;
using Inkshelf.Convert;

namespace Inkshelf.Endpoints;

public static class ConvertEndpoints
{
    public static void MapConvertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/convert/{id}", async (string id, string? fresh, string? warm,
            string? status, string? file, string? @return, HttpContext httpContext, ConvertService convert, DownloadMarks marks, CancellationToken ct) =>
        {
            var ds = DeviceSettings.Read(httpContext.Request);
            var t = ScreenTarget.FromCookie(httpContext.Request.Cookies["scr"], ds.Retina, ds.Grayscale, ds.Spread);

            if (status is "1")
            {
                var s = await convert.StatusAsync(id, t, ct, file);
                return s.Status == ConvertStatus.None ? Results.NotFound() : Results.Text(Text(s.Status));
            }

            var result = await convert.KickAsync(id, fresh is "1" or "true", t, ct, file);
            if (result.Status == ConvertStatus.None) return Results.NotFound();

            if (warm is "1")
                return result.Status == ConvertStatus.Done
                    ? Results.Text("done")
                    : Results.Text(Text(result.Status), statusCode: StatusCodes.Status202Accepted);

            if (result.Status != ConvertStatus.Done) return Results.Redirect(LocalReturn(@return));

            // Mark BEFORE streaming: we can't tell a completed transfer from an
            // aborted one anyway (see the spec), and the marker is advisory.
            var did = string.IsNullOrEmpty(ds.Did) ? DeviceSettings.Set(httpContext.Response, ds).Did : ds.Did;
            marks.Add(did, DownloadMarks.EpubKey(id, file));

            return Results.File(result.FilePath!, "application/epub+zip", fileDownloadName: result.DownloadName);
        });
    }

    private static string Text(ConvertStatus s) => s.ToString().ToLowerInvariant();

    // Open-redirect guard: only same-site absolute paths are honored.
    internal static string LocalReturn(string? r) =>
        !string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//") && !r.Contains('\\') ? r : "/";
}
