using System.Linq;
using Inkshelf.Abs;

namespace Inkshelf.Endpoints;

public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/download/{id}", async (string id, string? file, AbsApiClient api, HttpContext ctx, DownloadMarks marks, CancellationToken ct) =>
        {
            // Mark BEFORE streaming: we can't tell a completed transfer from an
            // aborted one anyway (see the spec), and the marker is advisory.
            static string EnsureDid(HttpContext ctx)
            {
                var s = Auth.DeviceSettings.Read(ctx.Request);
                return string.IsNullOrEmpty(s.Did) ? Auth.DeviceSettings.Set(ctx.Response, s).Did : s.Did;
            }

            try
            {
                var detail = await api.GetItemDetailAsync(id, ct);
                if (!string.IsNullOrEmpty(file))
                {
                    var lf = detail.LibraryFiles?.FirstOrDefault(f => f.Ino == file && f.FileType == "ebook");
                    var fname = lf?.Metadata?.Filename;
                    if (string.IsNullOrEmpty(fname)) return Results.NotFound();
                    var (fs, ftype) = await api.GetEbookFileStreamAsync(id, file, ct);
                    marks.Add(EnsureDid(ctx), DownloadMarks.RawKey(id, file));
                    return Results.File(fs, ftype, fileDownloadName: fname);
                }
                var name = detail.Media?.EbookFile?.Metadata?.Filename;
                if (string.IsNullOrEmpty(name)) return Results.NotFound();
                var (stream, contentType) = await api.GetEbookStreamAsync(id, ct);
                marks.Add(EnsureDid(ctx), DownloadMarks.RawKey(id, null));
                return Results.File(stream, contentType, fileDownloadName: name);
            }
            catch (HttpRequestException) { return Results.NotFound(); }
        });
    }
}
