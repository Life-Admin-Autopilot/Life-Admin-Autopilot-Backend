using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.VoiceNotes;

/// <summary>
/// How a background voice note reaches the person who recorded it.
///
/// <para>
/// <b>This is the other half of fire-and-forget.</b> The surface closes the instant
/// the upload is accepted, so from that moment the notification feed is the ONLY
/// place the outcome exists. A note that files three matters and holds two questions
/// and says nothing has, from the user's side, done nothing — and worse, it has done
/// something they cannot see. Two rows are written per note:
/// </para>
/// <list type="bullet">
///   <item>
///     one <c>uncertainty</c> row per question raised, carrying the question as its
///     title and linking BOTH the clarification and the matter it belongs to, so the
///     feed can open the card rather than dump the user on a list; and
///   </item>
///   <item>
///     one completion row per note, saying what happened in one line.
///   </item>
/// </list>
///
/// <para>
/// <b>The row is the record; the push is the delivery.</b> Writing the row is
/// mandatory and unconditional — it is what <c>GET /me/notifications</c> serves, and
/// dropping it would lose the outcome permanently. The push is best-effort, gated on
/// the user's own preference, and never allowed to fail the note: the same split
/// <c>ReminderTick</c> makes, for the same reason.
/// </para>
/// </summary>
public sealed class VoiceNoteOutcomeNotifier
{
    /// <summary>
    /// <c>kind: 'uncertainty'</c>. Declared in <see cref="NotificationVocabulary"/>
    /// since the schema was ported and, until now, never written by anything — the
    /// feed had a slot for "I need a decision from you" that nothing filled.
    /// </summary>
    public const string UncertaintyKind = "uncertainty";

    /// <summary>
    /// <c>kind: 'reminder'</c> for the completion row — NOT a voice-specific kind.
    /// The enum has no <c>voice_note</c> member, so inventing one here would emit a
    /// value the client's switch does not handle and the row would render as
    /// nothing.
    /// </summary>
    public const string CompletionKind = "reminder";

    public const string CompletionTitle = "Your voice note is filed";

    private readonly NotificationRepository _notifications;
    private readonly INotificationService _push;
    private readonly IDocumentScanNotifications _preferences;
    private readonly ILogger<VoiceNoteOutcomeNotifier> _logger;

    public VoiceNoteOutcomeNotifier(
        NotificationRepository notifications,
        INotificationService push,
        IDocumentScanNotifications preferences,
        ILogger<VoiceNoteOutcomeNotifier> logger)
    {
        _notifications = notifications;
        _push = push;
        _preferences = preferences;
        _logger = logger;
    }

    /// <summary>
    /// What the completion row says.
    ///
    /// <para>
    /// <b>Institutional voice: no exclamation marks, and the product noun is
    /// "matters".</b> The previous copy said "3 things filed · 2 guesses to confirm",
    /// which was a verbatim port of Node's string — but Node cannot reach this line
    /// on the parity target at all: with no model configured every note fails, so the
    /// reference server never writes a completion row and there is nothing here to
    /// diverge from. "Guesses to confirm" was also the wrong frame; it describes the
    /// machine's state rather than the reader's, and what the reader needs to know is
    /// that something is waiting on them.
    /// </para>
    /// </summary>
    public static string CompletionBody(int filed, int held)
    {
        var total = filed + held;
        if (total == 0)
        {
            return "Nothing to file from that one.";
        }

        var head = total == 1 ? "1 matter filed" : $"{total} matters filed";

        return held == 0 ? head : $"{head} · {held} need your input";
    }

    /// <summary>
    /// One question raised. Called once per clarification that was genuinely
    /// INSERTED — a worker reclaim re-runs the same upsert and must not re-notify a
    /// question the user has already seen, or a wedged note becomes a stream of
    /// duplicate rows for one recording.
    /// </summary>
    public async Task UncertaintyAsync(
        ObjectId userId,
        ObjectId taskId,
        ObjectId clarificationId,
        string question,
        string title,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        await _notifications.InsertAsync(
            new NotificationDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                Kind = UncertaintyKind,
                TaskId = taskId,
                ClarificationId = clarificationId,

                // The QUESTION is the title, not the matter's name. The row has one
                // job — get an answer — and a feed of matter names tells the reader
                // nothing about what is being asked. The matter's name is the body,
                // where it belongs as context.
                Title = question,
                Body = title,
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        await DeliverAsync(
            userId,
            question,
            title,
            new Dictionary<string, string>
            {
                ["kind"] = UncertaintyKind,
                ["taskId"] = taskId.ToString(),
                ["clarificationId"] = clarificationId.ToString(),
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One note finished. Guarded by the note's own <c>notifiedAt</c> outbox stamp.</summary>
    public async Task CompletedAsync(
        ObjectId userId,
        int filed,
        int held,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var body = CompletionBody(filed, held);

        await _notifications.InsertAsync(
            new NotificationDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                Kind = CompletionKind,
                Title = CompletionTitle,
                Body = body,
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        await DeliverAsync(
            userId,
            CompletionTitle,
            body,
            new Dictionary<string, string> { ["kind"] = "voice_note" },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Buzz the user's devices, if they want to be buzzed and there is a provider
    /// configured to do it.
    ///
    /// <para>
    /// <b>Every failure is swallowed, deliberately.</b> The row is already written by
    /// the time this runs, so an FCM outage, an expired service account or a user
    /// with no device registered must not fail the note — the note is finished and
    /// its work is durable. Re-running the worker to retry a PUSH would re-run
    /// extraction, which is far more expensive and far more dangerous than a missed
    /// buzz.
    /// </para>
    ///
    /// <para>
    /// <c>SendToUserAsync</c> keys devices by the JWT subject, which the session
    /// service signs as the Mongo user id — hence this string, not an Identity id.
    /// </para>
    /// </summary>
    private async Task DeliverAsync(
        ObjectId userId,
        string title,
        string body,
        Dictionary<string, string> data,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await _preferences.WantsPushAsync(userId, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await _push
                .SendToUserAsync(userId.ToString(), new PushMessage(title, body, data), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "voiceNote:push-failed userId={UserId} kind={Kind}", userId, data["kind"]);
        }
    }
}
