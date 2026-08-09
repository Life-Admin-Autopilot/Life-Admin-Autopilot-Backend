using System.Diagnostics;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot_Backend.Kernel.Modules;

namespace Life_Admin_Autopilot_Backend.Kernel.Health;

/// <summary>Wire shape of <c>GET /health</c>. Field order matches <c>routes/health.ts</c>.</summary>
public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "ok";

    [JsonPropertyName("db")]
    public string Db { get; init; } = MongoConnectionLabels.Connected;

    /// <summary>Seconds since process start, fractional — Node's <c>process.uptime()</c>.</summary>
    [JsonPropertyName("uptime")]
    public double Uptime { get; init; }
}

/// <summary>
/// The kernel's ONLY feature endpoint, and it exists as a smoke test: it proves
/// CORS, the JSON pipeline, Mongo connectivity and the module scanner are all
/// wired before a single slice lands.
///
/// <para>
/// <c>200 {"status":"ok","db":"connected","uptime":n}</c> when Mongo answers;
/// <c>503 {"status":"degraded","db":"disconnected","uptime":n}</c> when it does
/// not. It is deliberately NOT an error envelope — Node returns this same body
/// with a 503 status.
/// </para>
///
/// <para>It is also the pattern to copy: a module, a mapper, no <c>Program.cs</c> edit.</para>
/// </summary>
public sealed class HealthEndpointModule : IEndpointModule
{
    /// <summary>
    /// Process start, captured the first time this type is touched — which is
    /// during startup scanning, before the first request.
    /// </summary>
    private static readonly Stopwatch Since = Stopwatch.StartNew();

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", async (IMongoConnectionState state, CancellationToken cancellationToken) =>
        {
            var label = await state.GetLabelAsync(cancellationToken);
            var ready = label == MongoConnectionLabels.Connected;

            var body = new HealthResponse
            {
                Status = ready ? "ok" : "degraded",
                Db = label,
                Uptime = Since.Elapsed.TotalSeconds,
            };

            return Results.Json(body, statusCode: ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
        })
        .AllowAnonymous()
        .ExcludeFromDescription();
    }
}
