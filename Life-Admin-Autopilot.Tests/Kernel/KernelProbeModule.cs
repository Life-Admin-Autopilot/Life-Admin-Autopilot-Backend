using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// Test-only endpoints that exercise the kernel's binders and error paths
/// end-to-end. Discovered by the scanner because this assembly's name starts with
/// <c>Life-Admin-Autopilot</c> — and only ever loaded in a test run.
///
/// <para>It doubles as the worked example of the module convention: no
/// <c>Program.cs</c> edit, endpoints mapped from the slice's own file.</para>
/// </summary>
public sealed class KernelProbeModule : IEndpointModule
{
    public sealed class LenientBody
    {
        public string? Title { get; set; }

        public int Count { get; set; }
    }

    public sealed class StrictBody
    {
        public string? Title { get; set; }
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/__kernel-probe");

        group.MapPost("/lenient-body", async (HttpContext ctx) =>
        {
            var body = await KernelBody.ReadAsync<LenientBody>(
                ctx,
                KernelBodyOptions.Lenient("Invalid probe payload."));

            return Results.Ok(new { title = body.Title, count = body.Count });
        });

        group.MapPost("/strict-body", async (HttpContext ctx) =>
        {
            var body = await KernelBody.ReadAsync<StrictBody>(
                ctx,
                KernelBodyOptions.Strict_("Invalid task payload."));

            return Results.Ok(new { title = body.Title });
        });

        group.MapGet("/query", (HttpContext ctx) =>
        {
            var q = new QueryReader(ctx.Request.Query, "status", "q", "limit");
            var status = q.CsvEnum("status", TaskVocabulary.Statuses);
            var text = q.String("q", minLength: 1, maxLength: 200);
            var limit = q.Int("limit", min: 1, max: 200, fallback: 50);
            q.ThrowIfInvalid("invalid_query", "Invalid task list query.");

            return Results.Ok(new { status, q = text, limit });
        });

        group.MapGet("/objectid/{id}", (string id) =>
        {
            var parsed = MongoRepositoryBase<TaskDocument>.ParseObjectId(id);
            return Results.Ok(new { id = parsed.ToString() });
        });

        group.MapGet("/app-error", IResult () =>
            throw AppException.BadRequest(
                "invalid_body",
                "Some of those settings looked off.",
                ValidationDetails.AsFlattened(new[]
                {
                    ValidationIssue.At("theme", ZodMessages.InvalidEnum(UserVocabulary.Themes, "nope")),
                    ValidationIssue.At("mic.quality", ZodMessages.InvalidEnum(UserVocabulary.MicQualities, "ultra")),
                })));

        group.MapGet("/zod-error", IResult () =>
            throw new ValidationException(ValidationIssue.At("email", ZodMessages.InvalidEmail)));

        group.MapGet("/boom", IResult () => throw new InvalidOperationException("kaboom"));

        group.MapGet("/me", (HttpContext ctx) =>
        {
            var user = ctx.RequireUser();
            return Results.Ok(new { id = user.IdString, email = user.Email });
        }).RequireAuthorization();

        group.MapPost("/raw", async (HttpContext ctx) =>
        {
            var upload = await RawBodyReader.ReadAsync(
                ctx,
                maxBytes: 1024,
                emptyMessage: "No audio payload received.",
                tooLargeMessage: "Voice note exceeds 1MB.");

            var headers = RawBodyReader.Headers(ctx);
            var durationMs = headers.Int("x-voice-note-duration-ms", "durationMs", min: 0, max: 600_000);
            headers.ThrowIfInvalid("invalid_metadata", "Missing or invalid x-voice-note-* headers.");

            return Results.Ok(new { bytes = upload.Bytes.Length, contentType = upload.ContentType, durationMs });
        });
    }
}

/// <summary>Marker so <c>[Authorize]</c> resolves without a policy in the probe module.</summary>
internal static class ProbeAuthorization
{
    public static readonly AuthorizeAttribute Default = new();
}
