using System.Text;

namespace Inkshelf.Endpoints;

public static class DiagEndpoints
{
    private const int MaxBytes = 4096;

    public static void MapDiagEndpoints(this IEndpointRouteBuilder app)
    {
        // Receives the /diag.html browser capability probe and logs it, so device
        // limitations can be collected without a screenshot. No auth (pre-login tool)
        // — so the body is bounded and sanitized before logging, and the whole
        // endpoint is only mapped when enabled (see Program.cs / DIAG_ENABLED).
        app.MapPost("/diag", async (HttpContext ctx, ILogger<DiagLog> logger, CancellationToken ct) =>
        {
            // Read at most MaxBytes; never drain an unbounded body into memory/logs.
            var buffer = new byte[MaxBytes];
            var total = 0;
            int read;
            while (total < MaxBytes &&
                   (read = await ctx.Request.Body.ReadAsync(buffer.AsMemory(total, MaxBytes - total), ct)) > 0)
                total += read;

            logger.LogInformation("Browser probe: {Probe}", SanitizeProbe(Encoding.UTF8.GetString(buffer, 0, total)));
            return Results.Ok();
        });
    }

    // Neutralize control characters (incl. CR/LF, so a probe body can't forge log
    // lines) and cap the length. Pure — unit-tested directly.
    internal static string SanitizeProbe(string raw)
    {
        if (raw.Length > MaxBytes) raw = raw[..MaxBytes];
        var sb = new StringBuilder(raw.Length);
        foreach (var c in raw)
            sb.Append(char.IsControl(c) ? '.' : c);
        return sb.ToString();
    }

    // Log-category marker for the /diag endpoint.
    public sealed class DiagLog { }
}
