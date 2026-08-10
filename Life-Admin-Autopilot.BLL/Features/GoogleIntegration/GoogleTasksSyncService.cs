using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Integrations;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

public sealed class GoogleTasksSyncResult
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "synced";

    [JsonPropertyName("created")]
    public int Created { get; init; }

    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

public interface IGoogleTasksSyncService
{
    Task<GoogleTasksSyncResult> SyncAsync(
        IntegrationDocument integration,
        GoogleSyncOptions options,
        DateTime now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Import Google Tasks as matters. Port of
/// <c>server/src/modules/integrations/google/syncGoogleTasks.ts</c>.
///
/// <para>Two properties of the API shape everything here:</para>
/// <list type="number">
///   <item>
///     <b><c>due</c> IS DATE-ONLY</b>, by documented design. Every imported task
///     therefore goes through <see cref="ImportedTimeResolver"/> and lands on the
///     user's OWN stated import time. We never invent one.
///   </item>
///   <item>
///     <b>There are no webhooks.</b> The Tasks API has no <c>watch</c> method,
///     unlike Calendar. Polling with <c>updatedMin</c> is the only option, which
///     makes the idempotent upsert in the reconciler load-bearing rather than
///     defensive.
///   </item>
/// </list>
///
/// <para>
/// Note also that <c>showDeleted</c> and <c>showHidden</c> both default to FALSE, so
/// a naive call never sees completions or cleared items and the matter stays open in
/// Kitto forever.
/// </para>
/// </summary>
public sealed class GoogleTasksSyncService : IGoogleTasksSyncService
{
    public const string HttpClientName = "google-tasks";

    private const string Api = "https://tasks.googleapis.com/tasks/v1";

    /// <summary>The API's own ceiling for tasks.list.</summary>
    private const int PageSize = 100;

    /// <summary>Stop a runaway account from pulling forever in one tick.</summary>
    private const int MaxPages = 20;

    /// <summary>
    /// Re-read a little before the last sync so an item updated during the previous
    /// request is not missed in the gap between "read" and "recorded".
    /// </summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly IGoogleConnectionService _connections;
    private readonly ExternalMatterReconciler _reconciler;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleTasksSyncService> _logger;

    public GoogleTasksSyncService(
        IGoogleConnectionService connections,
        ExternalMatterReconciler reconciler,
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleTasksSyncService> logger)
    {
        _connections = connections;
        _reconciler = reconciler;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<GoogleTasksSyncResult> SyncAsync(
        IntegrationDocument integration,
        GoogleSyncOptions options,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        // The consent screen lets a user grant Calendar and decline Tasks. Calling
        // anyway would 403 on every tick with nothing explaining why.
        if (!_connections.HasScope(integration, GoogleOAuthClient.ScopeTasks))
        {
            return new GoogleTasksSyncResult { Status = "skipped", Reason = "Tasks access was not granted." };
        }

        var accessToken = await _connections.GetAccessTokenAsync(integration, cancellationToken).ConfigureAwait(false);

        // Node reads `integration.updatedAt`, which the access-token refresh above may
        // have just bumped. Ported as-is.
        var since = integration.UpdatedAt == default
            ? (DateTime?)null
            : integration.UpdatedAt - Overlap;

        var created = 0;
        var updated = 0;

        foreach (var list in await ListTaskListsAsync(accessToken, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(list.Id))
            {
                continue;
            }

            foreach (var task in await ListTasksAsync(list.Id, accessToken, since, cancellationToken)
                         .ConfigureAwait(false))
            {
                if (string.IsNullOrEmpty(task.Id))
                {
                    continue;
                }

                // A task deleted upstream is not our business to recreate, and Kitto
                // has no delete-propagation story yet — leaving the local matter alone
                // is the conservative choice over silently removing something the user
                // may have edited here.
                if (task.Deleted == true)
                {
                    continue;
                }

                var title = task.Title?.Trim();

                // Google permits empty titles; a blank matter helps nobody.
                if (string.IsNullOrEmpty(title))
                {
                    continue;
                }

                // A task with no due date is not a reminder — Kitto would have nothing
                // to fire on. Skipped rather than filed as an undated list item so an
                // import does not dump someone's entire backlog into the matters list.
                if (string.IsNullOrEmpty(task.Due))
                {
                    continue;
                }

                ResolvedDueAt resolved;
                try
                {
                    // Taking the date half is not lossy — there is nothing in the other
                    // half to lose.
                    resolved = ImportedTimeResolver.ResolveDateOnly(
                        task.Due.Length >= 10 ? task.Due[..10] : task.Due,
                        options.Timezone,
                        options.DefaultTimeOfDay);
                }
                catch (Exception ex) when (ex is TimezoneRequiredException or FormatException)
                {
                    _logger.LogWarning(ex, "googleTasks:unresolvable-due taskId={TaskId}", task.Id);
                    continue;
                }

                var notes = task.Notes?.Trim();

                var outcome = await _reconciler.ReconcileAsync(
                        new ExternalMatterInput(
                            integration.UserId,
                            "google_tasks",
                            task.Id,
                            title,
                            options.Domain,
                            resolved.DueAt,
                            "reminder",
                            resolved.Precision,
                            resolved.Confidence,
                            string.IsNullOrEmpty(notes) ? null : notes,
                            Completed: task.Status == "completed"),
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
        }

        return new GoogleTasksSyncResult { Status = "synced", Created = created, Updated = updated };
    }

    private async Task<List<GoogleTaskList>> ListTaskListsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        var page = await GetJsonAsync<GoogleTaskListsPage>(
                $"{Api}/users/@me/lists?maxResults=100", accessToken, cancellationToken)
            .ConfigureAwait(false);

        return page.Items ?? new List<GoogleTaskList>();
    }

    private async Task<List<GoogleTaskItem>> ListTasksAsync(
        string listId,
        string accessToken,
        DateTime? updatedMin,
        CancellationToken cancellationToken)
    {
        var all = new List<GoogleTaskItem>();
        string? pageToken = null;

        for (var page = 0; page < MaxPages; page += 1)
        {
            var query = new List<(string Key, string Value)>
            {
                ("maxResults", PageSize.ToString()),

                // All three default to false. Without them a completed or cleared task
                // simply vanishes from the response, which is indistinguishable from
                // "unchanged" — so Kitto would keep nudging about something already done.
                ("showDeleted", "true"),
                ("showHidden", "true"),
                ("showCompleted", "true"),
            };

            if (updatedMin.HasValue)
            {
                query.Add(("updatedMin", Iso(updatedMin.Value)));
            }

            if (!string.IsNullOrEmpty(pageToken))
            {
                query.Add(("pageToken", pageToken));
            }

            var url = $"{Api}/lists/{Uri.EscapeDataString(listId)}/tasks?{GoogleOAuthClient.FormUrlEncode(query)}";
            var json = await GetJsonAsync<GoogleTasksPage>(url, accessToken, cancellationToken).ConfigureAwait(false);

            if (json.Items is not null)
            {
                all.AddRange(json.Items);
            }

            pageToken = json.NextPageToken;
            if (string.IsNullOrEmpty(pageToken))
            {
                break;
            }
        }

        return all;
    }

    private async Task<T> GetJsonAsync<T>(string url, string accessToken, CancellationToken cancellationToken)
        where T : new()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await _httpClientFactory
            .CreateClient(HttpClientName)
            .SendAsync(request, timeout.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Tasks returned {(int)response.StatusCode}");
        }

        return await response.Content
            .ReadFromJsonAsync<T>(cancellationToken: timeout.Token)
            .ConfigureAwait(false) ?? new T();
    }

    private static string Iso(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
