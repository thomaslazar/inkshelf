using Inkshelf.Auth;
using Inkshelf.Convert;

namespace Inkshelf.Endpoints;

public static class ConvertEndpoints
{
    public static void MapConvertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/convert/{id}", async (string id, string? fresh, string? warm,
            string? status, string? file, string? @return, string? t, HttpContext httpContext, ConvertService convert,
            DownloadMarks marks, DownloadTickets tickets, CancellationToken ct) =>
        {
            // Redeem unconditionally: a poll carries the same href, and re-stamping
            // there is what keeps a long conversion's link alive.
            var tk = tickets.Redeem(t);
            // A ticket serves bytes and nothing else — no kick, no poll, no fresh.
            // It also ignores the request's render target and file param entirely, so a
            // still-live link after a screen-settings change replays the pre-change EPUB.
            if (status is not "1" && warm is not "1" && fresh is not ("1" or "true")
                && tk is { FilePath: { } cached } && tk.ItemId == id && File.Exists(cached))
            {
                marks.Add(tk.Did, DownloadMarks.EpubKey(id, tk.FileIno));
                return Results.File(cached, "application/epub+zip",
                    fileDownloadName: tk.DownloadName, enableRangeProcessing: true);
            }

            var ds = DeviceSettings.Read(httpContext.Request);
            var target = ds.ToRenderTarget(httpContext.Request.Cookies["scr"]);

            if (status is "1")
            {
                var s = await convert.StatusAsync(id, target, ct, file);
                return s.Status == ConvertStatus.None ? Results.NotFound() : Results.Text(Text(s.Status));
            }

            var result = await convert.KickAsync(id, fresh is "1" or "true", target, ct, file);
            if (result.Status == ConvertStatus.None) return Results.NotFound();

            if (warm is "1")
                return result.Status == ConvertStatus.Done
                    ? Results.Text("done")
                    : Results.Text(Text(result.Status), statusCode: StatusCodes.Status202Accepted);

            if (result.Status != ConvertStatus.Done) return Results.Redirect(LocalReturn(@return));

            // Mark BEFORE streaming: we can't tell a completed transfer from an
            // aborted one anyway, and the marker is advisory (see the spec).
            // Mint the did here, not at the top, so a status/warm poll never
            // writes a settings cookie.
            marks.Add(DeviceSettings.EnsureDid(httpContext).Did, DownloadMarks.EpubKey(id, file));

            return Results.File(result.FilePath!, "application/epub+zip", fileDownloadName: result.DownloadName,
                enableRangeProcessing: true);
        }).RespondsWithoutHtml();
    }

    private static string Text(ConvertStatus s) => s.ToString().ToLowerInvariant();

    // Open-redirect guard: only same-site absolute paths are honored.
    internal static string LocalReturn(string? r) =>
        !string.IsNullOrEmpty(r) && r.StartsWith('/') && !r.StartsWith("//") && !r.Contains('\\') ? r : "/";
}
