using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Features.VoiceNotes;

/// <summary>
/// The two read routes: list and fetch-one. Neither is rate limited in Node, and
/// neither takes a query parameter.
/// </summary>
public static class VoiceNoteReadEndpoints
{
    public const string NotFoundCode = "voice_note_not_found";
    public const string NotFoundMessage = "Voice note no longer exists.";

    public static IEndpointRouteBuilder MapVoiceNoteReads(this IEndpointRouteBuilder endpoints)
    {
        // GET /me/voice-notes — a hard-coded 50, newest first. No pagination, no
        // total, no filters.
        endpoints.MapGet("/me/voice-notes", async (
            HttpContext context,
            IVoiceNoteRepository notes,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var found = await notes.ListForUserAsync(caller.Id, cancellationToken);

            return Results.Ok(new VoiceListResponse
            {
                VoiceNotes = found.Select(n => n.ToDto()).ToList(),
            });
        })
        .RequireAuthorization();

        endpoints.MapGet("/me/voice-notes/{id}", async (
            HttpContext context,
            string id,
            IVoiceNoteRepository notes,
            CancellationToken cancellationToken) =>
        {
            var caller = context.RequireUser();
            var note = await FindOrThrowAsync(notes, id, caller.Id, cancellationToken);

            return Results.Ok(new VoiceSingleResponse { VoiceNote = note.ToDto() });
        })
        .RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// Owner-scoped lookup. A malformed id throws <c>ObjectIdCastException</c>
    /// first, which the kernel renders as the global 404 <c>not_found</c> — a
    /// DIFFERENT body from this route's own <c>voice_note_not_found</c>, and both
    /// are part of the contract.
    /// </summary>
    internal static async Task<VoiceNoteDocument> FindOrThrowAsync(
        IVoiceNoteRepository notes,
        string id,
        ObjectId userId,
        CancellationToken cancellationToken) =>
        await notes.FindForUserAsync(
            MongoRepositoryBase<VoiceNoteDocument>.ParseObjectId(id),
            userId,
            cancellationToken)
        ?? throw AppException.NotFound(NotFoundCode, NotFoundMessage);
}
