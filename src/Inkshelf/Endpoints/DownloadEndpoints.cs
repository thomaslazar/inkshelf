using System.Linq;
using Inkshelf.Abs;

namespace Inkshelf.Endpoints;

public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/download/{id}", async (string id, string? file, AbsApiClient api, CancellationToken ct) =>
        {
            try
            {
                var detail = await api.GetItemDetailAsync(id, ct);
                if (!string.IsNullOrEmpty(file))
                {
                    var lf = detail.LibraryFiles?.FirstOrDefault(f => f.Ino == file && f.FileType == "ebook");
                    var fname = lf?.Metadata?.Filename;
                    if (string.IsNullOrEmpty(fname)) return Results.NotFound();
                    var (fs, ftype) = await api.GetEbookFileStreamAsync(id, file, ct);
                    return Results.File(fs, ftype, fileDownloadName: fname);
                }
                var name = detail.Media?.EbookFile?.Metadata?.Filename;
                if (string.IsNullOrEmpty(name)) return Results.NotFound();
                var (stream, contentType) = await api.GetEbookStreamAsync(id, ct);
                return Results.File(stream, contentType, fileDownloadName: name);
            }
            catch (HttpRequestException) { return Results.NotFound(); }
        });
    }
}
