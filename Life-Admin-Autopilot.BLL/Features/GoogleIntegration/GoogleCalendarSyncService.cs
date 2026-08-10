using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <param name="Commitments">Meetings and recurring slots — seen, deliberately not filed as matters.</param>
/// <param name="FullResync">True when a 410 forced us to discard the cursor and re-read everything.</param>
public sealed class GoogleCalendarSyncResult
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "synced";

    [JsonPropertyName("created")]
    public int Created { get; init; }

    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    [JsonPropertyName("commitments")]
    public int Commitments { get; init; }

    [JsonPropertyName("ignored")]
    public int Ignored { get; init; }

    [JsonPropertyName("fullResync")]
    public bool FullResync { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

/// <summary>Where imported items are filed, and in whose clock.</summary>
public sealed record GoogleSyncOptions(string Timezone, string? DefaultTimeOfDay, string Domain);

public interface IGoogleCalendarSyncService
{
    Task<GoogleCalendarSyncResult> SyncAsync(
        IntegrationDocument integration,
        GoogleSyncOptions options,
        DateTime now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Import Google Calendar events. Port of
/// <c>server/src/modules/integrations/google/syncGoogleCalendar.ts</c>.
///
/// <para>Three behaviours are load-bearing:</para>
/// <list type="number">
///   <item>
///     <b>A 410 Gone is an INSTRUCTION, not a failure.</b> Google expires sync
///     tokens on its own schedule; the documented response is to wipe the local
///     cursor and do a full sync. Code that treats 410 as an error gets stuck
///     permanently, because the token never becomes valid again.
///   </item>
///   <item>
///     <c>singleEvents=true</c> makes Google expand recurrence for us, against an
///     API that will do it correctly.
///   </item>
///   <item>
///     A sync-token request <b>cannot</b> carry <c>timeMin</c>/<c>timeMax</c> —
///     Google rejects the combination. So the window is applied on the full sync
///     only, and incremental results are filtered locally.
///   </item>
/// </list>
///
/// <para>
/// Events are triaged before anything is written: only <c>matter</c> becomes a Kitto
/// matter. <c>commitment</c> is counted and reported but not persisted — nothing
/// consumes busy blocks yet, and storing data with no reader is just a migration to
/// write later.
/// </para>
/// </summary>
public sealed class GoogleCalendarSyncService : IGoogleCalendarSyncService
{
    public const string HttpClientName = "google-calendar";

    private const string Api = "https://www.googleapis.com/calendar/v3";
    private const int PageSize = 250;
    private const int MaxPages = 20;
    private const int WindowBackDays = 7;
    private const int WindowForwardDays = 365;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly IGoogleConnectionService _connections;
    private readonly IIntegrationRepository _integrations;
    private readonly ExternalMatterReconciler _reconciler;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleCalendarSyncService> _logger;

    public GoogleCalendarSyncService(
        IGoogleConnectionService connections,
        IIntegrationRepository integrations,
        ExternalMatterReconciler reconciler,
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleCalendarSyncService> logger)
    {
        _connections = connections;
        _integrations = integrations;
        _reconciler = reconciler;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<GoogleCalendarSyncResult> SyncAsync(
        IntegrationDocument integration,
        GoogleSyncOptions options,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (!_connections.HasScope(integration, GoogleOAuthClient.ScopeCalendar))
        {
            return new GoogleCalendarSyncResult
            {
                Status = "skipped",
                Reason = "Calendar access was not granted.",
            };
        }

        var accessToken = await _connections.GetAccessTokenAsync(integration, cancellationToken).ConfigureAwait(false);

        var fullResync = false;
        List<GoogleCalendarEvent> events;
        string? nextSyncToken;

        try
        {
            (events, nextSyncToken) = await ReadEventsAsync(
                    accessToken, integration.CalendarSyncToken, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SyncTokenExpiredException)
        {
            // Documented recovery, not a failure path.
            _logger.LogInformation(
                "googleCalendar:sync-token-expired integrationId={IntegrationId}",
                integration.Id);

            fullResync = true;
            integration.CalendarSyncToken = null;
            (events, nextSyncToken) = await ReadEventsAsync(accessToken, null, now, cancellationToken)
                .ConfigureAwait(false);
        }

        var from = now.AddDays(-WindowBackDays);
        var to = now.AddDays(WindowForwardDays);

        var created = 0;
        var updated = 0;
        var commitments = 0;
        var ignored = 0;

        foreach (var e in events)
        {
            var role = GoogleEventTriage.Triage(e);
            if (role == EventRole.Ignore)
            {
                ignored += 1;
                continue;
            }

            if (role == EventRole.Commitment)
            {
                commitments += 1;
                continue;
            }

            if (string.IsNullOrEmpty(e.Id))
            {
                continue;
            }

            var title = e.Summary?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            DateTime dueAt;
            string precision;
            string confidence;

            if (!string.IsNullOrEmpty(e.Start?.DateTime))
            {
                if (!DateTimeOffset.TryParse(
                        e.Start.DateTime,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.RoundtripKind,
                        out var parsed))
                {
                    continue;
                }

                dueAt = parsed.UtcDateTime;
                precision = "exact";
                confidence = "high";
            }
            else if (!string.IsNullOrEmpty(e.Start?.Date))
            {
                try
                {
                    var resolved = ImportedTimeResolver.ResolveDateOnly(
                        e.Start.Date, options.Timezone, options.DefaultTimeOfDay);
                    dueAt = resolved.DueAt;
                    precision = "dateOnly";
                    confidence = "high";
                }
                catch (Exception ex) when (ex is TimezoneRequiredException or FormatException)
                {
                    _logger.LogWarning(ex, "googleCalendar:unresolvable-start eventId={EventId}", e.Id);
                    continue;
                }
            }
            else
            {
                continue;
            }

            // Incremental results are not window-bounded (syncToken and timeMin cannot
            // be combined), so the filter is applied here instead.
            if (dueAt < from || dueAt > to)
            {
                continue;
            }

            // Does Google already alert on this? `useDefault` means the calendar's own
            // default reminders apply, which for most people is a popup before the
            // event — so both branches count.
            var sourceHasOwnAlerts =
                e.Reminders?.UseDefault == true || (e.Reminders?.Overrides?.Count ?? 0) > 0;

            var notes = e.Location?.Trim();

            var outcome = await _reconciler.ReconcileAsync(
                    new ExternalMatterInput(
                        integration.UserId,
                        "google_calendar",
                        e.Id,
                        title,
                        options.Domain,
                        dueAt,
                        "reminder",
                        precision,
                        confidence,
                        string.IsNullOrEmpty(notes) ? null : notes,
                        Completed: false,
                        SourceHasOwnAlerts: sourceHasOwnAlerts),
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            if (outcome.Created)
            {
                created += 1;
            }

            if (outcome.Updated)
            {
                updated += 1;
            }
        }

        if (!string.IsNullOrEmpty(nextSyncToken))
        {
            integration.CalendarSyncToken = nextSyncToken;
        }

        integration.CalendarSyncedAt = now;
        integration.UpdatedAt = now;
        await _integrations.SaveAsync(integration, cancellationToken).ConfigureAwait(false);

        return new GoogleCalendarSyncResult
        {
            Status = "synced",
            Created = created,
            Updated = updated,
            Commitments = commitments,
            Ignored = ignored,
            FullResync = fullResync,
        };
    }

    private async Task<(List<GoogleCalendarEvent> Events, string? NextSyncToken)> ReadEventsAsync(
        string accessToken,
        string? syncToken,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var events = new List<GoogleCalendarEvent>();
        string? pageToken = null;
        string? nextSyncToken = null;

        for (var page = 0; page < MaxPages; page += 1)
        {
            var query = new List<(string Key, string Value)>
            {
                ("maxResults", PageSize.ToString()),
                ("singleEvents", "true"),

                // Needed to see deletions: a removed event arrives as a cancelled
                // tombstone rather than simply vanishing.
                ("showDeleted", "true"),
            };

            if (!string.IsNullOrEmpty(syncToken))
            {
                query.Add(("syncToken", syncToken));
            }
            else
            {
                // Only legal on a full sync — Google rejects timeMin alongside syncToken.
                query.Add(("timeMin", Iso(now.AddDays(-WindowBackDays))));
                query.Add(("timeMax", Iso(now.AddDays(WindowForwardDays))));
                query.Add(("orderBy", "startTime"));
            }

            if (!string.IsNullOrEmpty(pageToken))
            {
                query.Add(("pageToken", pageToken));
            }

            var url = $"{Api}/calendars/primary/events?{GoogleOAuthClient.FormUrlEncode(query)}";
            var json = await GetPageAsync(url, accessToken, cancellationToken).ConfigureAwait(false);

            if (json.Items is not null)
            {
                events.AddRange(json.Items);
            }

            // The sync token only appears on the LAST page. Storing one from a middle
            // page would silently skip every event on the pages after it.
            if (!string.IsNullOrEmpty(json.NextSyncToken))
            {
                nextSyncToken = json.NextSyncToken;
            }

            pageToken = json.NextPageToken;
            if (string.IsNullOrEmpty(pageToken))
            {
                break;
            }
        }

        return (events, nextSyncToken);
    }

    private async Task<GoogleEventsPage> GetPageAsync(
        string url,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClientFactory
            .CreateClient(HttpClientName)
            .SendAsync(request, timeout.Token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new SyncTokenExpiredException();
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Calendar returned {(int)response.StatusCode}");
        }

        return await response.Content
            .ReadFromJsonAsync<GoogleEventsPage>(cancellationToken: timeout.Token)
            .ConfigureAwait(false) ?? new GoogleEventsPage();
    }

    /// <summary>JS <c>Date#toISOString()</c> — always three fractional digits and a Z.</summary>
    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Internal control flow for the 410, never surfaced to a caller.</summary>
    private sealed class SyncTokenExpiredException : Exception;
}
