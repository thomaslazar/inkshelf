using System.Linq;
using Inkshelf.Abs;
using Inkshelf.Auth;

namespace Inkshelf.Endpoints;

public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/download/{id}", async (string id, string? file, string? t, AbsApiClient api, DownloadTickets tickets,
            AbsDownloadClient dl, HttpContext ctx, DownloadMarks marks, CancellationToken ct) =>
        {
            // A cookie-less download manager (issue #40): the ticket carries both the
            // filename and the ABS bearer, so this path needs neither the cookie nor
            // an item-detail lookup.
            if (tickets.Redeem(t) is { Access: { } access } tk && tk.ItemId == id)
            {
                try
                {
                    var (stream, type, len) = await dl.DownloadEbookAsync(id, access, ct, tk.FileIno);
                    marks.Add(tk.Did, DownloadMarks.RawKey(id, tk.FileIno));
                    ctx.Response.ContentLength = len;
                    return Results.File(stream, type, fileDownloadName: tk.DownloadName);
                }
                // A ticket is additive, never a gate: if ABS rejects its bearer, fall
                // through to the cookie path — which a browser request has and which
                // refreshes. Nothing is written to the response yet (AbsDownloadClient
                // disposes and throws before yielding a stream), so this is clean.
                catch (HttpRequestException) { }
            }

            try
            {
                var detail = await api.GetItemDetailAsync(id, ct);
                if (!string.IsNullOrEmpty(file))
                {
                    var lf = detail.LibraryFiles?.FirstOrDefault(f => f.Ino == file && f.FileType == "ebook");
                    var fname = lf?.Metadata?.Filename;
                    if (string.IsNullOrEmpty(fname)) return Results.NotFound();
                    var (fs, ftype, flen) = await api.GetEbookFileStreamAsync(id, file, ct);
                    marks.Add(DeviceSettings.EnsureDid(ctx).Did, DownloadMarks.RawKey(id, file));
                    ctx.Response.ContentLength = flen;
                    return Results.File(fs, ftype, fileDownloadName: fname);
                }
                var name = detail.Media?.EbookFile?.Metadata?.Filename;
                if (string.IsNullOrEmpty(name)) return Results.NotFound();
                var (stream, contentType, length) = await api.GetEbookStreamAsync(id, ct);
                marks.Add(DeviceSettings.EnsureDid(ctx).Did, DownloadMarks.RawKey(id, null));
                // ABS knows the size; the stream is a live network stream, so
                // Results.File cannot work it out and the response would go out
                // chunked. Ranges stay unadvertised — we can't serve them.
                ctx.Response.ContentLength = length;
                return Results.File(stream, contentType, fileDownloadName: name);
            }
            catch (HttpRequestException) { return Results.NotFound(); }
        }).RespondsWithoutHtml();
    }
}
