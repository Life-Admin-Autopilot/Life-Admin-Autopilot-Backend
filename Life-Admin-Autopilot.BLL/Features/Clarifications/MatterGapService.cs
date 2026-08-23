using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Features.Clarifications;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Clarifications;

/// <summary>
/// Questions raised about a matter that is ALREADY SAVED.
///
/// <para>
/// <b>The order is the point: file first, ask second.</b> Every other question in
/// this codebase is raised by <see cref="ClarificationHoldService"/>, which creates
/// the task and the question in one write. That is right for the agent's own
/// <c>holdForClarification</c> — it decided to ask before anything existed. It is
/// the wrong shape for the gap the agent did NOT notice: by then the matter is
/// saved, and the only honest thing left to do is ask about it as an edit to the
/// row the user can already see. A second <c>hold</c> would file the matter twice.
/// </para>
///
/// <para>
/// <b>Why this is not left to the prompt.</b> The chat agent is instructed to hold
/// any matter that arrives without a date. Measured on 2026-08-22 it did so for
/// "اشتري لبن" every time and for "تعلم سباحة" never — the model reads the second
/// as an aspiration and reasons past the instruction, and no rewording moved it. A
/// missing date is a fact about the saved document, so it is checked here, after the
/// write, where the model cannot skip it.
/// </para>
///
/// <para>
/// <b>The user still cannot lose the matter.</b> Whatever happens here, the task is
/// already written and already on the list. A question that cannot be filed — the
/// queue is full, the insert throws — costs the user a card, never the thought. That
/// is the opposite of the failure this replaced, where a <c>hold</c> that returned
/// an error made the chat say "I couldn't record your request" about a matter that
/// had never been created at all.
/// </para>
/// </summary>
public sealed class MatterGapService
{
    private readonly ClarificationRepository _clarifications;
    private readonly IMongoCollection<UserProfileDocument> _users;

    public MatterGapService(ClarificationRepository clarifications, IMongoDatabase database)
    {
        _clarifications = clarifications;
        _users = database.GetCollection<UserProfileDocument>(MongoCollections.Users);
    }

    /// <summary>
    /// Ask about whatever this saved matter is missing, and return the rows filed.
    ///
    /// <para>
    /// Empty means nothing was missing, OR that the open-question cap is already
    /// reached. The two are deliberately not distinguished to the caller: both mean
    /// "no question was raised", the matter is saved either way, and the cap exists
    /// precisely so that a user who is behind on answering is not handed more.
    /// </para>
    /// </summary>
    /// <param name="timeAssumed">
    /// True when the agent picked the clock time rather than being told it. See
    /// <see cref="VoiceAutoFilePolicy.GapsFor"/>.
    /// </param>
    public async Task<IReadOnlyList<ClarificationDocument>> FileGapsAsync(
        ObjectId userId,
        TaskDocument task,
        string? timezone,
        bool timeAssumed,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var zone = VoiceAutoFilePolicy.ResolveZone(timezone)
            ?? await ProfileZoneAsync(userId, cancellationToken).ConfigureAwait(false);

        // Only the fields the gap tests read are real here. Confidence is 1 and the
        // conflict list is empty on purpose: a saved matter has no confidence score,
        // and clashes are checked by the caller that saved it — the agent's tool
        // posts to /me/tasks/{id}/conflicts the moment a dated task lands.
        var draft = new TaskDraft(
            task.Title,
            task.Domain,
            task.Priority,
            task.Kind,
            task.DueAt,
            task.Notes,
            SourceType: "chat",
            Confidence: 1,
            Conflicts: Array.Empty<PlanningConflict>(),
            TimeAssumed: timeAssumed,
            Amount: task.Amount);

        var gaps = VoiceAutoFilePolicy.GapsFor(draft, zone, timeAssumed);
        if (gaps.Count == 0)
        {
            return Array.Empty<ClarificationDocument>();
        }

        // The same cap the hold path enforces, counted the same way. Asked for once
        // and spent down locally rather than re-counted per row, so a two-gap matter
        // with one slot left files the FIRST gap — which is the date, the one that
        // decides whether the matter can ever resurface.
        var open = await _clarifications.CountOpenAsync(userId, cancellationToken).ConfigureAwait(false);
        var capacity = (int)(ClarificationHoldService.MaxOpenClarifications - open);
        if (capacity <= 0)
        {
            return Array.Empty<ClarificationDocument>();
        }

        var rows = new List<ClarificationDocument>(gaps.Count);

        foreach (var gap in gaps.Take(capacity))
        {
            var asked = ChatGapText.InTheLanguageOfTheMatter(gap, task.Title, zone);

            var row = new ClarificationDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                TaskId = task.Id,
                Status = ClarificationVocabulary.Open,

                // Every row describes the same saved matter. The draft is what the
                // card renders beside the question and what the resolve path reads
                // back, so it mirrors the row as filed rather than as proposed.
                Draft = new ClarificationDraftDocument
                {
                    Title = task.Title,
                    Domain = task.Domain,
                    Priority = task.Priority,
                    Notes = task.Notes,
                    Tags = task.Tags?.ToList() ?? new List<string>(),
                    DueAt = task.DueAt,
                },

                // Finished text, in the language of the matter's own title, and NO
                // key — this is the chat lane, where the user's language is known.
                // Keyed, the card rendered in whatever the app was set to, so an
                // Arabic conversation in an English app answered itself in English
                // directly under the model's Arabic question. See ChatGapText.
                Question = asked.Question,
                QuestionKey = null,
                QuestionParams = null,
                Kind = asked.Kind,
                CostOfWrong = asked.CostOfWrong,
                Options = asked.Options
                    .Select(o => new ClarificationOptionDocument
                    {
                        Label = o.Label,
                        DueAt = o.DueAt,
                    })
                    .ToList(),

                // No sourceKey, for the reason the chat hold has none: that is the
                // voice lane's note-scoped idempotency key and the partial unique
                // index is keyed on its presence.
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _clarifications.InsertAsync(row, cancellationToken).ConfigureAwait(false);
            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// The zone on the user's profile, when the caller supplied none.
    ///
    /// <para>
    /// <b>Not a nicety — without it the chips are three hours wrong.</b> Every
    /// option here carries a resolved instant AND a label naming its local hour,
    /// and both come from the zone. With none, "Tomorrow — 09:00" is composed in
    /// UTC and files 09:00Z, which is noon in Cairo: the label and the instant
    /// disagree, and the matter lands at a time the user never saw offered.
    /// </para>
    ///
    /// <para>
    /// The caller's value still wins — it is the device's live zone, and a user
    /// abroad is somewhere their profile has not caught up with. But the agent
    /// only sends a timezone argument when it is composing a date, and a matter
    /// with NO date is exactly the case where it sends nothing. That is the case
    /// this exists for.
    /// </para>
    ///
    /// <para>
    /// Null on a profile with no zone, and null on a throw. The gap questions
    /// still get asked; their chips are composed in UTC, which is the behaviour
    /// before a zone was consulted at all.
    /// </para>
    /// </summary>
    private async Task<string?> ProfileZoneAsync(ObjectId userId, CancellationToken cancellationToken)
    {
        try
        {
            var stored = await _users
                .Find(Builders<UserProfileDocument>.Filter.Eq(u => u.Id, userId))
                .Project(u => u.Timezone)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return VoiceAutoFilePolicy.ResolveZone(stored);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
