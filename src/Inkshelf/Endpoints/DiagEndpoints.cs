namespace Inkshelf.Endpoints;

public static class DiagEndpoints
{
    public static void MapDiagEndpoints(this IEndpointRouteBuilder app)
    {
        // Receives the /diag.html browser capability probe and logs it, so device
        // limitations can be collected without a screenshot. No auth (pre-login tool).
        app.MapPost("/diag", async (HttpContext ctx, ILogger<DiagLog> logger) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            logger.LogInformation("Browser probe: {Probe}", body);
            return Results.Ok();
        });
    }

    // Log-category marker for the /diag endpoint.
    public sealed class DiagLog { }
}
