using Inkshelf.Abs;

namespace Inkshelf.Endpoints;

public static class CoverEndpoints
{
    public static void MapCoverEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/cover/{id}", async (string id, int? w, AbsApiClient api, CancellationToken ct) =>
        {
            var width = w is > 0 and <= 400 ? w.Value : 120;
            try
            {
                var (stream, contentType) = await api.GetCoverAsync(id, width, ct);
                return Results.Stream(stream, contentType);
            }
            catch (HttpRequestException)
            {
                // Item has no cover (ABS 404) or a transient fetch error — the <img>
                // just shows nothing rather than the page 500ing.
                return Results.NotFound();
            }
        });
    }
}
