using Inkshelf.Abs;

namespace Inkshelf.Endpoints;

public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/download/{id}", async (string id, AbsApiClient api, CancellationToken ct) =>
        {
            try
            {
                var detail = await api.GetItemDetailAsync(id, ct);
                var name = detail.Media?.EbookFile?.Metadata?.Filename;
                if (string.IsNullOrEmpty(name)) return Results.NotFound();
                var (stream, contentType) = await api.GetEbookStreamAsync(id, ct);
                return Results.File(stream, contentType, fileDownloadName: name);
            }
            catch (HttpRequestException) { return Results.NotFound(); }
        });
    }
}
