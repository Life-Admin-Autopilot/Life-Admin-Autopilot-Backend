using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Kernel;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Auth;

namespace Life_Admin_Autopilot_Backend.Features.Ai;

/// <summary>
/// Multi-conversation routes — the history drawer's whole API surface.
///
/// <para>
/// These sit alongside the older single-conversation
/// <c>GET /ai/conversation</c> + <c>POST /ai/conversation/reset</c>, which keep
/// working: a shipped Capacitor binary cannot be recalled, so those resolve to the
/// user's most recent thread instead of 404ing.
/// </para>
///
/// <para>
/// Like their neighbours in <see cref="AiConversationEndpoints"/>, none of these
/// is rate limited or gated on the model being configured — the drawer must render
/// on a deployment with no agent behind it.
/// </para>
/// </summary>
public static class AiThreadEndpoints
{
    public static IEndpointRouteBuilder MapAiThreadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // GET /ai/conversations — every thread, most recently active first.
        endpoints.MapGet("/ai/conversations", async (
            HttpContext context,
            AiConversationThreadService threads,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            return Results.Ok(new AiThreadListResponse
            {
                Conversations = await threads.ListAsync(caller.Id, cancellationToken).ConfigureAwait(false),
            });
        })
        .RequireAuthorization();

        // POST /ai/conversations — start a thread. The body is optional; a thread
        // with no seed title is named by its first message instead.
        endpoints.MapPost("/ai/conversations", async (
            HttpContext context,
            CreateConversationRequest? body,
            AiConversationThreadService threads,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var created = await threads
                .CreateAsync(caller.Id, body?.Title, cancellationToken)
                .ConfigureAwait(false);

            return Results.Created($"/ai/conversations/{created.Id}", created);
        })
        .RequireAuthorization();

        // GET /ai/conversations/{id} — one thread's transcript.
        endpoints.MapGet("/ai/conversations/{id}", async (
            string id,
            HttpContext context,
            AiConversationThreadService threads,
            AiConversationRepository conversations,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            // Existence is checked BEFORE the load: LoadAsync upserts, so calling it
            // with an unknown id would silently manufacture the thread the caller
            // asked about and answer 200 for something that never existed.
            if (!await threads.ExistsAsync(caller.Id, id, cancellationToken).ConfigureAwait(false))
            {
                throw new AppException(404, "conversation_not_found", "This conversation no longer exists.");
            }

            var document = await conversations
                .LoadAsync(caller.Id, AiConversationVocabulary.PersonalScope, id, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new AiThreadResponse
            {
                Id = id,
                Title = document.Title,
                Messages = document.Messages.Select(m => m.ToDto()).ToList(),
            });
        })
        .RequireAuthorization();

        // PATCH /ai/conversations/{id} — rename.
        endpoints.MapPatch("/ai/conversations/{id}", async (
            string id,
            RenameConversationRequest body,
            HttpContext context,
            AiConversationThreadService threads,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            var title = body?.Title?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                throw new AppException(400, "invalid_body", "A conversation name is required.");
            }

            if (!await threads.RenameAsync(caller.Id, id, title, cancellationToken).ConfigureAwait(false))
            {
                throw new AppException(404, "conversation_not_found", "This conversation no longer exists.");
            }

            return Results.Ok(new RenameConversationResponse { Id = id, Title = title });
        })
        .RequireAuthorization();

        // DELETE /ai/conversations/{id} — drop the thread and its transcript.
        endpoints.MapDelete("/ai/conversations/{id}", async (
            string id,
            HttpContext context,
            AiConversationThreadService threads,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();

            if (!await threads.DeleteAsync(caller.Id, id, cancellationToken).ConfigureAwait(false))
            {
                throw new AppException(404, "conversation_not_found", "This conversation no longer exists.");
            }

            return Results.NoContent();
        })
        .RequireAuthorization();

        return endpoints;
    }
}

public sealed class CreateConversationRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

public sealed class RenameConversationRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

public sealed class RenameConversationResponse
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;
}
