using Inkshelf.Abs;

namespace Inkshelf.Endpoints;

public static class DownloadEndpoints
{
    public static void MapDownloadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/download/{id}", async (string id, AbsSession session, AbsClient client, CancellationToken ct) =>
        {
            try
            {
                var detail = await session.ExecuteAsync((tok, c) => client.GetItemDetailAsync(tok, id, c), ct);
                var name = detail.Media?.EbookFile?.Metadata?.Filename;
                if (string.IsNullOrEmpty(name)) return Results.NotFound();
                var (stream, contentType) = await session.ExecuteAsync((tok, c) => client.GetEbookStreamAsync(tok, id, c), ct);
                return Results.File(stream, contentType, fileDownloadName: name);
            }
            catch (HttpRequestException) { return Results.NotFound(); }
        });
    }
}
