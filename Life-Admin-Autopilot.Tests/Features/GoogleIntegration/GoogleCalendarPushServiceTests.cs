using System.Net;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Life_Admin_Autopilot.DAL.Features.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.GoogleIntegration;

/// <summary>
/// The outbound mirror: matters written INTO a Kitto-owned Google calendar.
///
/// <para>
/// Run against real Mongo because the whole design lives in the queries — the
/// service decides what to push by asking the database which matters SHOULD have an
/// event, and stubbing that away would leave the actual logic untested. Skipped
/// when no parity Mongo is reachable, like the other repository suites.
/// </para>
/// </summary>
public sealed class GoogleCalendarPushServiceTests
{
    private const string PushDatabase = "kitto_parity_dotnet_google_push_tests";

    private static readonly DateTime Now = new(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);

    // ---- What gets mirrored ----------------------------------------------

    [Fact]
    public async Task pushes_a_dated_open_matter_and_records_the_event_id()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        var task = await SeedTaskAsync(db, userId, "Renew the car licence", Now.AddDays(2));

        var google = new FakeGoogle();
        var result = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(1, result.Created);

        var inserted = Assert.Single(google.Inserted);
        Assert.Equal("Renew the car licence", inserted.GetProperty("summary").GetString());

        var stored = await FindAsync(db, task.Id);
        Assert.Equal(FakeGoogle.NewEventId, stored.GoogleEventId);
        Assert.NotNull(stored.GooglePushedAt);
    }

    // The point of the app-created scope: Kitto writes to a calendar it made, never
    // to the user's own.
    [Fact]
    public async Task creates_its_own_calendar_once_and_reuses_it()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "First", Now.AddDays(1));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(1, google.CalendarsCreated);
        Assert.Equal(FakeGoogle.CalendarId, integration.PushCalendarId);

        await SeedTaskAsync(db, userId, "Second", Now.AddDays(3));
        await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(1, google.CalendarsCreated);
    }

    // Connecting an account must not litter it with an empty calendar.
    [Fact]
    public async Task creates_no_calendar_when_there_is_nothing_to_mirror()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, _) = await ConnectedAsync(db);

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(0, google.CalendarsCreated);
        Assert.Null(integration.PushCalendarId);
    }

    // ---- The loop guard ---------------------------------------------------
    //
    // A matter imported FROM Google is already on the user's calendar. Pushing it
    // back makes a second copy, which the next import reads as a new matter — one
    // appointment becoming fifty.

    [Fact]
    public async Task never_pushes_back_a_matter_that_came_from_google()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Dentist", Now.AddDays(1), externalSource: "google_calendar");

        var google = new FakeGoogle();
        var result = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(0, result.Created);
        Assert.Empty(google.Inserted);
    }

    [Fact]
    public async Task never_pushes_back_a_matter_from_any_external_feed()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Lecture", Now.AddDays(1), externalSource: "ics_feed");

        var google = new FakeGoogle();
        Assert.Equal(0, (await Service(db, google).PushAsync(integration, Now)).Created);
    }

    // ---- Not everything belongs on a calendar -----------------------------

    [Fact]
    public async Task skips_a_matter_with_no_date()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Buy milk", dueAt: null);

        var google = new FakeGoogle();
        Assert.Equal(0, (await Service(db, google).PushAsync(integration, Now)).Created);
    }

    [Fact]
    public async Task skips_a_matter_far_outside_the_window()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Passport in three years", Now.AddDays(1200));

        var google = new FakeGoogle();
        Assert.Equal(0, (await Service(db, google).PushAsync(integration, Now)).Created);
    }

    // ---- Edits follow ------------------------------------------------------

    [Fact]
    public async Task patches_the_existing_event_rather_than_inserting_a_duplicate()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        var task = await SeedTaskAsync(db, userId, "Old title", Now.AddDays(2));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        // Edited after the push, exactly as PatchAsync leaves the row.
        await Tasks(db).UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
            Builders<TaskDocument>.Update
                .Set(t => t.Title, "New title")
                .Set(t => t.UpdatedAt, Now.AddMinutes(5)));

        var result = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Created);
        Assert.Single(google.Inserted);
        Assert.Equal("New title", Assert.Single(google.Patched).GetProperty("summary").GetString());
    }

    [Fact]
    public async Task leaves_an_unchanged_matter_alone()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Steady", Now.AddDays(2));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);
        var second = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(0, second.Created);
        Assert.Equal(0, second.Updated);
    }

    // ---- Removals ----------------------------------------------------------
    //
    // The cases a timestamp diff would miss: BulkService writes deletedAt WITHOUT
    // bumping updatedAt, so the reconciler works from the row's state instead.

    [Fact]
    public async Task removes_the_event_when_the_matter_is_soft_deleted()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        var task = await SeedTaskAsync(db, userId, "Cancelled thing", Now.AddDays(2));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        // Exactly what BulkService does — deletedAt only, updatedAt untouched.
        await Tasks(db).UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
            Builders<TaskDocument>.Update.Set(t => t.DeletedAt, Now));

        var result = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(1, result.Removed);
        Assert.Equal(FakeGoogle.NewEventId, Assert.Single(google.Deleted));

        var stored = await FindAsync(db, task.Id);
        Assert.Null(stored.GoogleEventId);
    }

    [Fact]
    public async Task removes_the_event_when_the_matter_is_completed()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        var task = await SeedTaskAsync(db, userId, "Done thing", Now.AddDays(2));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        await Tasks(db).UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
            Builders<TaskDocument>.Update.Set(t => t.Status, "done"));

        Assert.Equal(1, (await Service(db, google).PushAsync(integration, Now)).Removed);
    }

    // Clearing the link must not look like an edit, or a completed matter would be
    // re-pushed next pass and deleted the pass after, forever.
    [Fact]
    public async Task a_removal_settles_instead_of_flapping()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        var task = await SeedTaskAsync(db, userId, "Done thing", Now.AddDays(2));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        await Tasks(db).UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
            Builders<TaskDocument>.Update.Set(t => t.Status, "done"));

        await Service(db, google).PushAsync(integration, Now);
        var third = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(0, third.Created);
        Assert.Equal(0, third.Removed);
        Assert.Single(google.Inserted);
        Assert.Single(google.Deleted);
    }

    // A user deleting the event by hand should not orphan the matter forever.
    [Fact]
    public async Task recreates_an_event_the_user_deleted_by_hand()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        var task = await SeedTaskAsync(db, userId, "Comes back", Now.AddDays(2));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        google.NextPatchIsNotFound = true;
        await Tasks(db).UpdateOneAsync(
            Builders<TaskDocument>.Filter.Eq(t => t.Id, task.Id),
            Builders<TaskDocument>.Update.Set(t => t.UpdatedAt, Now.AddMinutes(5)));

        var result = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(1, result.Created);
        Assert.Equal(2, google.Inserted.Count);
    }

    // ---- Times are what the user reads --------------------------------------
    //
    // The first version sent a bare `...Z` instant. Unambiguous to a machine, wrong
    // to a person: Google renders it against the VIEWER's display timezone, so a
    // matter saved for 10:00 in Cairo showed up at 07:00 — same moment, wrong
    // number, and the number is the only part anyone reads.

    [Fact]
    public async Task writes_the_users_wall_clock_time_not_a_utc_instant()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);

        // 07:00Z IS 10:00 in Cairo — the instant the app stores for "10 am".
        await SeedTaskAsync(db, userId, "Swimming", new DateTime(2026, 8, 15, 7, 0, 0, DateTimeKind.Utc));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        var start = Assert.Single(google.Inserted).GetProperty("start");

        Assert.Equal("2026-08-15T10:00:00", start.GetProperty("dateTime").GetString());
        Assert.Equal("Africa/Cairo", start.GetProperty("timeZone").GetString());
    }

    // An offset alongside `timeZone` overrides it and reinstates the original bug.
    [Fact]
    public async Task never_writes_an_offset_alongside_the_zone()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Swimming", new DateTime(2026, 8, 15, 7, 0, 0, DateTimeKind.Utc));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        var written = Assert.Single(google.Inserted)
            .GetProperty("start").GetProperty("dateTime").GetString()!;

        Assert.DoesNotContain("Z", written, StringComparison.Ordinal);
        Assert.DoesNotContain("+", written, StringComparison.Ordinal);
    }

    // Google defaults a new secondary calendar to UTC, which is what put every
    // event three hours out for a Cairo user.
    [Fact]
    public async Task creates_the_calendar_in_the_users_zone()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Anything", Now.AddDays(1));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        Assert.Equal("Africa/Cairo", Assert.Single(google.CreatedCalendars).GetProperty("timeZone").GetString());
        Assert.Equal("Africa/Cairo", integration.PushCalendarTimeZone);
    }

    // A calendar built before the zone was known has to be repaired IN PLACE —
    // retimed, and every event already in it rewritten rather than duplicated.
    [Fact]
    public async Task repairs_a_calendar_that_was_created_without_a_zone()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        var task = await SeedTaskAsync(db, userId, "Swimming", new DateTime(2026, 8, 15, 7, 0, 0, DateTimeKind.Utc));

        var google = new FakeGoogle();
        await Service(db, google).PushAsync(integration, Now);

        // Rewind to the broken shape: calendar exists, zone never recorded.
        integration.PushCalendarTimeZone = null;
        await new IntegrationRepository(db).SaveAsync(integration);

        var result = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal("Africa/Cairo", Assert.Single(google.RetimedCalendars));
        Assert.Equal("Africa/Cairo", integration.PushCalendarTimeZone);

        // Repaired, not duplicated.
        Assert.Equal(1, result.Updated);
        Assert.Single(google.Inserted);
        Assert.Equal("2026-08-15T10:00:00", Assert.Single(google.Patched)
            .GetProperty("start").GetProperty("dateTime").GetString());

        var stored = await FindAsync(db, task.Id);
        Assert.Equal(FakeGoogle.NewEventId, stored.GoogleEventId);
    }

    [Fact]
    public async Task skips_the_push_entirely_when_the_account_has_no_timezone()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Anything", Now.AddDays(1));

        var google = new FakeGoogle();
        var result = await Service(db, google, timezone: null).PushAsync(integration, Now);

        Assert.Equal("skipped", result.Status);
        Assert.Equal(0, google.CalendarsCreated);
        Assert.Empty(google.Inserted);
    }

    // ---- The scope gate -----------------------------------------------------

    [Fact]
    public async Task skips_an_account_connected_before_the_write_scope_existed()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db, new[] { GoogleOAuthClient.ScopeCalendar });
        await SeedTaskAsync(db, userId, "Not pushed", Now.AddDays(1));

        var google = new FakeGoogle();
        var result = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal("skipped", result.Status);
        Assert.Empty(google.Inserted);
        Assert.Equal(0, google.CalendarsCreated);
    }

    // ---- One user's matters never reach another's calendar -------------------

    [Fact]
    public async Task pushes_only_the_owners_matters()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var (integration, userId) = await ConnectedAsync(db);
        await SeedTaskAsync(db, userId, "Mine", Now.AddDays(1));
        await SeedTaskAsync(db, ObjectId.GenerateNewId(), "Someone else's", Now.AddDays(1));

        var google = new FakeGoogle();
        var result = await Service(db, google).PushAsync(integration, Now);

        Assert.Equal(1, result.Created);
        Assert.Equal("Mine", Assert.Single(google.Inserted).GetProperty("summary").GetString());
    }

    // ---- Helpers -------------------------------------------------------------

    private static GoogleCalendarPushService Service(
        IMongoDatabase db,
        FakeGoogle google,
        string? timezone = "Africa/Cairo") =>
        new(
            new StubConnections(),
            new IntegrationRepository(db),
            new StubProfiles(timezone),
            new TaskRepository(db),
            google.Factory(),
            NullLogger<GoogleCalendarPushService>.Instance);

    private static IMongoCollection<TaskDocument> Tasks(IMongoDatabase db) =>
        new TaskRepository(db).Tasks;

    /// <summary>A fresh user per test, so the suites do not need a clean database.</summary>
    private static async Task<(IntegrationDocument Integration, ObjectId UserId)> ConnectedAsync(
        IMongoDatabase db,
        string[]? scopes = null)
    {
        var userId = ObjectId.GenerateNewId();
        var integration = new IntegrationDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            Provider = IntegrationVocabulary.Google,
            ExternalAccountId = "google-user",
            RefreshTokenEnc = "enc",
            Status = IntegrationVocabulary.StatusActive,
            GrantedScopes = (scopes ?? new[]
            {
                GoogleOAuthClient.ScopeCalendar,
                GoogleOAuthClient.ScopeCalendarApp,
            }).ToList(),
            ConnectedAt = Now,
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        await new IntegrationRepository(db).SaveAsync(integration);
        return (integration, userId);
    }

    private static async Task<TaskDocument> SeedTaskAsync(
        IMongoDatabase db,
        ObjectId userId,
        string title,
        DateTime? dueAt = null,
        string? externalSource = null)
    {
        var task = new TaskDocument
        {
            Id = ObjectId.GenerateNewId(),
            UserId = userId,
            Title = title,
            Domain = "home",
            Kind = dueAt is null ? "list" : "reminder",
            Status = "open",
            Priority = "normal",
            DueAt = dueAt,
            ExternalSource = externalSource,
            ExternalId = externalSource is null ? null : "ext-1",
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        await Tasks(db).InsertOneAsync(task);
        return task;
    }

    private static Task<TaskDocument> FindAsync(IMongoDatabase db, ObjectId id) =>
        Tasks(db).Find(Builders<TaskDocument>.Filter.Eq(t => t.Id, id)).SingleAsync();

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings).GetDatabase(PushDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The account's stored timezone, which decides how events render.</summary>
    private sealed class StubProfiles : IGoogleImportProfileReader
    {
        private readonly string? _timezone;

        public StubProfiles(string? timezone) => _timezone = timezone;

        public Task<GoogleImportProfile?> FindAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<GoogleImportProfile?>(new GoogleImportProfile(_timezone, null));

        public Task<Dictionary<ObjectId, GoogleImportProfile>> FindManyAsync(
            IReadOnlyCollection<ObjectId> userIds,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(userIds.ToDictionary(id => id, _ => new GoogleImportProfile(_timezone, null)));
    }

    /// <summary>Token plumbing belongs to the sync services, not to this one.</summary>
    private sealed class StubConnections : IGoogleConnectionService
    {
        public Task<IntegrationDocument?> FindAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IntegrationDocument?>(null);

        public Task<IntegrationDocument> SaveConnectionAsync(
            ObjectId userId,
            GoogleTokens tokens,
            GoogleIdentity? identity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetAccessTokenAsync(
            IntegrationDocument integration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("test-access-token");

        public Task DisconnectAsync(IntegrationDocument integration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public bool HasScope(IntegrationDocument integration, string scope) =>
            integration.GrantedScopes.Contains(scope);
    }

    /// <summary>A Calendar API that records what it was asked to do.</summary>
    private sealed class FakeGoogle
    {
        public const string CalendarId = "kitto-calendar-id";
        public const string NewEventId = "event-1";

        public int CalendarsCreated { get; private set; }

        public List<JsonElement> CreatedCalendars { get; } = new();

        /// <summary>The zone each calendar PATCH set.</summary>
        public List<string> RetimedCalendars { get; } = new();

        public List<JsonElement> Inserted { get; } = new();

        public List<JsonElement> Patched { get; } = new();

        public List<string> Deleted { get; } = new();

        public bool NextPatchIsNotFound { get; set; }

        public IHttpClientFactory Factory() => new SingleClientFactory(new HttpClient(new Handler(this)));

        private sealed class Handler : HttpMessageHandler
        {
            private readonly FakeGoogle _state;

            public Handler(FakeGoogle state) => _state = state;

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                var body = request.Content is null
                    ? (JsonElement?)null
                    : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement;

                if (request.Method == HttpMethod.Post && path.EndsWith("/calendars", StringComparison.Ordinal))
                {
                    _state.CalendarsCreated += 1;
                    _state.CreatedCalendars.Add(body!.Value.Clone());
                    return Json($"{{\"id\":\"{CalendarId}\"}}");
                }

                // A calendar PATCH — retiming — rather than an event PATCH.
                if (request.Method == HttpMethod.Patch && !path.Contains("/events", StringComparison.Ordinal))
                {
                    _state.RetimedCalendars.Add(body!.Value.GetProperty("timeZone").GetString()!);
                    return Json($"{{\"id\":\"{CalendarId}\"}}");
                }

                if (request.Method == HttpMethod.Post && path.EndsWith("/events", StringComparison.Ordinal))
                {
                    _state.Inserted.Add(body!.Value.Clone());
                    return Json($"{{\"id\":\"{NewEventId}\"}}");
                }

                if (request.Method == HttpMethod.Patch)
                {
                    if (_state.NextPatchIsNotFound)
                    {
                        _state.NextPatchIsNotFound = false;
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }

                    _state.Patched.Add(body!.Value.Clone());
                    return Json($"{{\"id\":\"{NewEventId}\"}}");
                }

                if (request.Method == HttpMethod.Delete)
                {
                    _state.Deleted.Add(path[(path.LastIndexOf('/') + 1)..]);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }

                return new HttpResponseMessage(HttpStatusCode.NotImplemented);
            }

            private static HttpResponseMessage Json(string payload) =>
                new(HttpStatusCode.OK) { Content = new StringContent(payload) };
        }

        private sealed class SingleClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public SingleClientFactory(HttpClient client) => _client = client;

            public HttpClient CreateClient(string name) => _client;
        }
    }
}
