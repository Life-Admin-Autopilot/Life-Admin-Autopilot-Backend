using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>What one push pass did.</summary>
public sealed class GooglePushResult
{
    public string Status { get; init; } = "synced";

    public int Created { get; init; }

    public int Updated { get; init; }

    public int Removed { get; init; }

    public string? Reason { get; init; }
}

public interface IGoogleCalendarPushService
{
    Task<GooglePushResult> PushAsync(
        IntegrationDocument integration,
        DateTime now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Mirror the user's dated matters into a Kitto-owned calendar in THEIR Google
/// account — the outbound half of the integration.
///
/// <para>
/// <b>A reconciler, not a change feed.</b> It asks "which matters should have an
/// event, and which have one they should not" and fixes the difference, rather than
/// replaying edits. That is not a stylistic preference: deletes and completions go
/// through <c>BulkService</c>, which writes <c>deletedAt</c> without touching
/// <c>updatedAt</c>, so a timestamp diff would never notice a deleted matter — the
/// one case where leaving a stale event on someone's calendar is worst. Desired
/// state is derived from the row itself and cannot miss a path.
/// </para>
///
/// <para>
/// <b>Never touches a calendar Kitto did not make.</b> The token carries
/// <c>calendar.app.created</c>, which Google restricts to app-created calendars, so
/// this is enforced on the server side and not merely by careful code here.
/// </para>
/// </summary>
public sealed class GoogleCalendarPushService : IGoogleCalendarPushService
{
    public const string HttpClientName = "google-calendar-push";

    private const string Api = "https://www.googleapis.com/calendar/v3";

    /// <summary>Shown in the user's calendar list. They may rename it freely.</summary>
    private const string CalendarSummary = "Kitto";

    private const string CalendarDescription =
        "Matters from your Kitto life admin assistant. Safe to hide or delete — Kitto rebuilds it.";

    /// <summary>How far ahead to mirror. Matches the import window's forward reach.</summary>
    private const int WindowForwardDays = 365;

    /// <summary>And how far back, so a just-missed matter is still visible.</summary>
    private const int WindowBackDays = 7;

    /// <summary>A matter with no duration still needs one on a calendar.</summary>
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>One pass is bounded so a huge backlog cannot monopolise the worker.</summary>
    private const int MaxPerPass = 200;

    private readonly IGoogleConnectionService _connections;
    private readonly IIntegrationRepository _integrations;
    private readonly IGoogleImportProfileReader _profiles;
    private readonly TaskRepository _tasks;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleCalendarPushService> _logger;

    public GoogleCalendarPushService(
        IGoogleConnectionService connections,
        IIntegrationRepository integrations,
        IGoogleImportProfileReader profiles,
        TaskRepository tasks,
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleCalendarPushService> logger)
    {
        _connections = connections;
        _integrations = integrations;
        _profiles = profiles;
        _tasks = tasks;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<GooglePushResult> PushAsync(
        IntegrationDocument integration,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.HasScope(integration, GoogleOAuthClient.ScopeCalendarApp))
        {
            // Every account connected before the write scope existed lands here.
            // Skipped rather than failed: nothing is wrong, the user simply has not
            // reconnected, and a red error on the sheet would say otherwise.
            return new GooglePushResult
            {
                Status = "skipped",
                Reason = "Reconnect your Google account to let Kitto add matters to it.",
            };
        }

        // A calendar event is a LOCAL time to the person reading it. Without the
        // user's zone every event would have to be written as a bare UTC instant,
        // which renders correctly only for a viewer whose Google display timezone
        // happens to agree — and silently three hours early for a Cairo user.
        var profile = await _profiles.FindAsync(integration.UserId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(profile?.Timezone))
        {
            return new GooglePushResult
            {
                Status = "skipped",
                Reason = "Set your timezone before Kitto adds matters to your calendar.",
            };
        }

        var zone = profile.Value.Timezone!;

        var accessToken = await _connections.GetAccessTokenAsync(integration, cancellationToken).ConfigureAwait(false);

        var mirror = await MirrorableAsync(integration.UserId, now, cancellationToken).ConfigureAwait(false);
        var stale = await StaleAsync(integration.UserId, cancellationToken).ConfigureAwait(false);

        if (mirror.Count == 0 && stale.Count == 0 && integration.PushCalendarId is null)
        {
            // Nothing to mirror and no calendar yet — do not create an empty one in
            // someone's account just because they connected.
            return new GooglePushResult { Status = "synced" };
        }

        var calendarId = await EnsureCalendarAsync(integration, zone, accessToken, cancellationToken)
            .ConfigureAwait(false);

        // Re-read: retiming the calendar clears the push stamps, and the list taken
        // before that would otherwise miss every event needing a re-render.
        if (mirror.Count == 0 && stale.Count == 0)
        {
            mirror = await MirrorableAsync(integration.UserId, now, cancellationToken).ConfigureAwait(false);
        }

        var created = 0;
        var updated = 0;
        var removed = 0;

        foreach (var task in stale)
        {
            if (await DeleteEventAsync(calendarId, task.GoogleEventId!, accessToken, cancellationToken)
                .ConfigureAwait(false))
            {
                removed += 1;
            }

            await ClearLinkAsync(task, cancellationToken).ConfigureAwait(false);
        }

        foreach (var task in mirror)
        {
            var body = EventFor(task, zone);

            if (task.GoogleEventId is { Length: > 0 } eventId)
            {
                var patched = await PatchEventAsync(calendarId, eventId, body, accessToken, cancellationToken)
                    .ConfigureAwait(false);

                if (patched)
                {
                    updated += 1;
                    await StampAsync(task, eventId, now, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // 404: the user deleted the event by hand. Recreate it rather than
                // leaving the matter unmirrored forever — the calendar is Kitto's
                // to own, and a matter that still exists still belongs on it.
                _logger.LogInformation("googlePush:event-gone taskId={TaskId}", task.Id);
            }

            var inserted = await InsertEventAsync(calendarId, body, accessToken, cancellationToken)
                .ConfigureAwait(false);

            if (inserted is null) continue;

            created += 1;
            await StampAsync(task, inserted, now, cancellationToken).ConfigureAwait(false);
        }

        return new GooglePushResult
        {
            Status = "synced",
            Created = created,
            Updated = updated,
            Removed = removed,
        };
    }

    // ---- Desired state ----------------------------------------------------

    /// <summary>
    /// Matters that SHOULD have an event: open, dated, inside the window, and not
    /// themselves imported from somewhere else.
    /// </summary>
    private async Task<List<TaskDocument>> MirrorableAsync(
        ObjectId userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var filter = Builders<TaskDocument>.Filter.And(
            Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
            Builders<TaskDocument>.Filter.Eq(t => t.Status, "open"),
            Builders<TaskDocument>.Filter.Exists("deletedAt", false),
            Builders<TaskDocument>.Filter.Ne(t => t.DueAt, null),
            Builders<TaskDocument>.Filter.Gte(t => t.DueAt, now.AddDays(-WindowBackDays)),
            Builders<TaskDocument>.Filter.Lte(t => t.DueAt, now.AddDays(WindowForwardDays)),

            // The loop guard. A matter imported FROM Google already exists on the
            // user's calendar; mirroring it back would create a second copy, which
            // the next import would read as a new matter, and so on.
            Builders<TaskDocument>.Filter.Eq(t => t.ExternalSource, null),

            // Unpushed, or edited since the last push.
            Builders<TaskDocument>.Filter.Or(
                Builders<TaskDocument>.Filter.Eq(t => t.GooglePushedAt, null),
                Builders<TaskDocument>.Filter.Where(t => t.GooglePushedAt < t.UpdatedAt)));

        return await _tasks.Tasks
            .Find(filter)
            .Limit(MaxPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Matters that have an event they should NOT: deleted, completed, undated, or
    /// pushed out of the window.
    ///
    /// <para>
    /// Driven by the row's state rather than by a change notification, which is what
    /// makes the delete path immune to <c>BulkService</c> not stamping
    /// <c>updatedAt</c>.
    /// </para>
    /// </summary>
    private async Task<List<TaskDocument>> StaleAsync(ObjectId userId, CancellationToken cancellationToken)
    {
        var filter = Builders<TaskDocument>.Filter.And(
            Builders<TaskDocument>.Filter.Eq(t => t.UserId, userId),
            Builders<TaskDocument>.Filter.Ne(t => t.GoogleEventId, null),
            Builders<TaskDocument>.Filter.Or(
                Builders<TaskDocument>.Filter.Exists("deletedAt", true),
                Builders<TaskDocument>.Filter.Ne(t => t.Status, "open"),
                Builders<TaskDocument>.Filter.Eq(t => t.DueAt, null)));

        return await _tasks.Tasks
            .Find(filter)
            .Limit(MaxPerPass)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task StampAsync(TaskDocument task, string eventId, DateTime now, CancellationToken cancellationToken) =>
        _tasks.Tasks.UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
            Builders<TaskDocument>.Update
                .Set(t => t.GoogleEventId, eventId)
                .Set(t => t.GooglePushedAt, now),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Drops the link WITHOUT touching <c>updatedAt</c>.
    ///
    /// <para>
    /// Bumping it would make the matter look edited to every other reader — and for a
    /// completed matter that still satisfies <see cref="MirrorableAsync"/>'s other
    /// clauses, it would be re-pushed on the next pass, deleted on the one after,
    /// forever.
    /// </para>
    /// </summary>
    private Task ClearLinkAsync(TaskDocument task, CancellationToken cancellationToken) =>
        _tasks.Tasks.UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
            Builders<TaskDocument>.Update
                .Unset(t => t.GoogleEventId)
                .Unset(t => t.GooglePushedAt),
            cancellationToken: cancellationToken);

    // ---- The calendar -----------------------------------------------------

    private async Task<string> EnsureCalendarAsync(
        IntegrationDocument integration,
        string zone,
        string accessToken,
        CancellationToken cancellationToken)
    {
        if (integration.PushCalendarId is { Length: > 0 } existing)
        {
            if (!string.Equals(integration.PushCalendarTimeZone, zone, StringComparison.Ordinal))
            {
                await RetimeAsync(integration, existing, zone, accessToken, cancellationToken).ConfigureAwait(false);
            }

            return existing;
        }

        var created = await SendAsync<GoogleCalendarResource>(
                HttpMethod.Post,
                $"{Api}/calendars",
                new GoogleCalendarResource
                {
                    Summary = CalendarSummary,
                    Description = CalendarDescription,

                    // Omitting this is not neutral: Google defaults a new secondary
                    // calendar to UTC, and every event in it then reads three hours
                    // early to the Cairo user who created it.
                    TimeZone = zone,
                },
                accessToken,
                cancellationToken)
            .ConfigureAwait(false);

        var id = created?.Id
                 ?? throw new IntegrationUnavailableException("Google did not return the new calendar.");

        integration.PushCalendarId = id;
        integration.PushCalendarTimeZone = zone;
        integration.UpdatedAt = DateTime.UtcNow;
        await _integrations.SaveAsync(integration, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "googlePush:calendar-created integrationId={IntegrationId} zone={Zone}",
            integration.Id,
            zone);

        return id;
    }

    /// <summary>
    /// Move an existing calendar onto the right zone and force every event in it to
    /// be written again.
    ///
    /// <para>
    /// Retiming the calendar alone would leave already-written events at whatever
    /// they were rendered with, so the push stamps are cleared too — which is what
    /// makes the next pass re-render them. The event IDs are deliberately KEPT, so
    /// this repairs the existing events in place rather than duplicating them.
    /// </para>
    /// </summary>
    private async Task RetimeAsync(
        IntegrationDocument integration,
        string calendarId,
        string zone,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{Api}/calendars/{Uri.EscapeDataString(calendarId)}";
        var (status, _) = await RawAsync(
                HttpMethod.Patch,
                url,
                new GoogleCalendarResource { TimeZone = zone },
                accessToken,
                cancellationToken)
            .ConfigureAwait(false);

        if (status == HttpStatusCode.NotFound || status == HttpStatusCode.Gone)
        {
            // The user deleted the whole calendar. Forget it and let the next pass
            // build a fresh one rather than writing into something that is gone.
            integration.PushCalendarId = null;
            integration.PushCalendarTimeZone = null;
            integration.UpdatedAt = DateTime.UtcNow;
            await _integrations.SaveAsync(integration, cancellationToken).ConfigureAwait(false);

            throw new IntegrationUnavailableException("The Kitto calendar was removed; it will be rebuilt.");
        }

        EnsureSuccess(status, url);

        await _tasks.Tasks.UpdateManyAsync(
                Builders<TaskDocument>.Filter.And(
                    Builders<TaskDocument>.Filter.Eq(t => t.UserId, integration.UserId),
                    Builders<TaskDocument>.Filter.Ne(t => t.GoogleEventId, null)),
                Builders<TaskDocument>.Update.Unset(t => t.GooglePushedAt),
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        integration.PushCalendarTimeZone = zone;
        integration.UpdatedAt = DateTime.UtcNow;
        await _integrations.SaveAsync(integration, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "googlePush:calendar-retimed integrationId={IntegrationId} zone={Zone}",
            integration.Id,
            zone);
    }

    // ---- One event --------------------------------------------------------

    private static GoogleEventResource EventFor(TaskDocument task, string zone)
    {
        var start = task.DueAt!.Value;

        return new GoogleEventResource
        {
            Summary = task.Title,
            Description = Description(task),
            Start = TimeIn(start, zone),
            End = TimeIn(start.Add(DefaultDuration), zone),

            // Survives a rename of the calendar and lets a future importer recognise
            // its own output even if the local link is lost.
            ExtendedProperties = new GoogleExtendedProperties
            {
                Private = new Dictionary<string, string>
                {
                    ["kittoTaskId"] = task.Id.ToString(),
                },
            },
        };
    }

    private static string? Description(TaskDocument task)
    {
        var notes = task.Notes?.Trim();
        return string.IsNullOrEmpty(notes) ? null : notes;
    }

    private async Task<string?> InsertEventAsync(
        string calendarId,
        GoogleEventResource body,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var created = await SendAsync<GoogleEventResource>(
                HttpMethod.Post,
                $"{Api}/calendars/{Uri.EscapeDataString(calendarId)}/events",
                body,
                accessToken,
                cancellationToken)
            .ConfigureAwait(false);

        return created?.Id;
    }

    /// <summary>False when the event is gone (404) and the caller should reinsert.</summary>
    private async Task<bool> PatchEventAsync(
        string calendarId,
        string eventId,
        GoogleEventResource body,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{Api}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}";
        var (status, _) = await RawAsync(HttpMethod.Patch, url, body, accessToken, cancellationToken)
            .ConfigureAwait(false);

        if (status == HttpStatusCode.NotFound || status == HttpStatusCode.Gone) return false;

        EnsureSuccess(status, url);
        return true;
    }

    /// <summary>True when something was actually removed.</summary>
    private async Task<bool> DeleteEventAsync(
        string calendarId,
        string eventId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{Api}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}";
        var (status, _) = await RawAsync(HttpMethod.Delete, url, null, accessToken, cancellationToken)
            .ConfigureAwait(false);

        // Already gone is the desired end state, not a failure — the user may have
        // deleted it by hand, which is exactly what we were about to do.
        if (status == HttpStatusCode.NotFound || status == HttpStatusCode.Gone) return false;

        EnsureSuccess(status, url);
        return true;
    }

    // ---- Transport --------------------------------------------------------

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string url,
        object? body,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var (status, payload) = await RawAsync(method, url, body, accessToken, cancellationToken)
            .ConfigureAwait(false);

        EnsureSuccess(status, url);

        return string.IsNullOrWhiteSpace(payload)
            ? default
            : JsonSerializer.Deserialize<T>(payload);
    }

    private async Task<(HttpStatusCode Status, string Body)> RawAsync(
        HttpMethod method,
        string url,
        object? body,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: SerializerOptions);
        }

        using var response = await _httpClientFactory
            .CreateClient(HttpClientName)
            .SendAsync(request, timeout.Token)
            .ConfigureAwait(false);

        var payload = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        return (response.StatusCode, payload);
    }

    private static void EnsureSuccess(HttpStatusCode status, string url)
    {
        if (status is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices) return;

        // 401/403 here means the grant changed under us — the connection service owns
        // that transition, so this surfaces as unavailable rather than guessing.
        throw new IntegrationUnavailableException(
            $"Google refused a calendar write ({(int)status}) at {new Uri(url).AbsolutePath}.");
    }

    /// <summary>
    /// The instant as the user's own WALL CLOCK, plus the zone that makes it mean
    /// that.
    ///
    /// <para>
    /// A bare <c>...Z</c> instant is the obvious encoding and it is what shipped
    /// first. It is unambiguous to a machine and wrong for a person: Google renders
    /// it against the viewer's display timezone, so a matter saved for 10:00 in
    /// Cairo appeared at 07:00 — the same moment, the wrong number, which is the
    /// only thing the user actually reads. Sending the local time WITH its zone
    /// states the intent instead of leaving it to be re-derived.
    /// </para>
    ///
    /// <para>
    /// A zone the host cannot resolve falls back to UTC rather than failing the
    /// push: a matter on the calendar at an awkward offset beats no matter at all.
    /// </para>
    /// </summary>
    private static GoogleEventTime TimeIn(DateTime instant, string zone)
    {
        var utc = DateTime.SpecifyKind(instant, DateTimeKind.Utc);

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(zone);
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);

            // No trailing Z and no offset — `timeZone` is what resolves it, and an
            // offset here would override the zone and reintroduce the bug.
            return new GoogleEventTime
            {
                DateTime = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
                TimeZone = zone,
            };
        }
        catch (Exception)
        {
            return new GoogleEventTime
            {
                DateTime = utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            };
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- Wire shapes ------------------------------------------------------

    private sealed class GoogleCalendarResource
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("timeZone")]
        public string? TimeZone { get; set; }
    }

    private sealed class GoogleEventResource
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("start")]
        public GoogleEventTime? Start { get; set; }

        [JsonPropertyName("end")]
        public GoogleEventTime? End { get; set; }

        [JsonPropertyName("extendedProperties")]
        public GoogleExtendedProperties? ExtendedProperties { get; set; }
    }

    private sealed class GoogleEventTime
    {
        [JsonPropertyName("dateTime")]
        public string? DateTime { get; set; }

        [JsonPropertyName("timeZone")]
        public string? TimeZone { get; set; }
    }

    private sealed class GoogleExtendedProperties
    {
        [JsonPropertyName("private")]
        public Dictionary<string, string>? Private { get; set; }
    }
}
