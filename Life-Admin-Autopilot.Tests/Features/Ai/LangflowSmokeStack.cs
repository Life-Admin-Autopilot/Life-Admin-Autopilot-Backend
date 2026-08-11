using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.BLL.Features.Ai.Langflow;
using Life_Admin_Autopilot.DAL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The live stack the smoke suite talks to, and the two real turns it takes,
/// <b>run exactly once</b> for the whole class.
///
/// <para>
/// <b>Why a fixture instead of a turn per test.</b> Every turn is a paid model round
/// against a free tier that rate-limits hard, so the suite spends two asks and one
/// confirm and then asserts many separate things about them. Splitting the assertions
/// across named tests costs nothing and makes a failure say which invariant broke;
/// splitting the TURNS would triple the model calls and the flakiness.
/// </para>
///
/// <para>
/// <b>Every scenario runs as a brand-new signed-up user.</b> The seeded demo account
/// has 130 open matters and three sibling agents working against the same stack, so
/// nothing here could count rows deterministically or delete anything safely. A fresh
/// account makes "the store had 0 matters, now it has 1" an exact statement, and it is
/// also what makes a retry safe: an attempt that half-ran is abandoned along with its
/// user rather than re-entered.
/// </para>
///
/// <para>
/// <b>The adapter runs IN PROCESS; its side effects do not.</b> The provider under
/// test is the one this build compiled — pointing the suite at <c>:5080</c> for the
/// asks would measure whatever binary happens to be running there. But the flow's
/// eleven tools are HTTP wrappers that call the backend back with the caller's own
/// bearer token, so what they write lands in the running server's database, and that
/// is where the assertions look. The provider is therefore handed THAT database too,
/// so the confirm route can find the pending record this process staged.
/// </para>
/// </summary>
public sealed class LangflowSmokeStack : IAsyncLifetime
{
    /// <summary>
    /// Mistral's free tier answers a 429 as an <c>error</c> frame inside a healthy
    /// 200. Spacing turns is cheaper than retrying them; both are used.
    /// </summary>
    private const int DefaultSpacingSeconds = 45;

    private const int DefaultAttempts = 3;

    /// <summary>The database <c>stack.sh</c> points the running backend at.</summary>
    private const string DefaultDatabase = "kitto_dev";

    private const string DefaultApiBaseUrl = "http://localhost:5080";

    /// <summary>Meets the signup policy; used only for throwaway accounts on an isolated stack.</summary>
    private const string ScratchPassword = "Str0ngPassw0rd!23";

    private readonly HttpClient _api = new() { Timeout = TimeSpan.FromSeconds(240) };

    private IMongoDatabase? _database;
    private LangflowOptions? _options;
    private DateTimeOffset _lastTurnAt = DateTimeOffset.MinValue;

    // ---- what the tests read -------------------------------------------------

    /// <summary>False on a developer machine with no Langflow. See the honesty guard
    /// in <see cref="LangflowSmokeTests"/> — this is never silently true.</summary>
    public bool Configured { get; private set; }

    /// <summary>
    /// Configured, but the turns could not be taken. Re-thrown by every test rather
    /// than swallowed: "the live suite could not run" must never read as a pass.
    /// </summary>
    public Exception? SetupFailure { get; private set; }

    public CreatedMatterTurn Created { get; private set; } = CreatedMatterTurn.Empty;

    public GatedDeleteTurn Gated { get; private set; } = GatedDeleteTurn.Empty;

    // ---- lifecycle -----------------------------------------------------------

    public async Task InitializeAsync()
    {
        _options = SmokeOptions();
        _database = TryGetDatabase();
        Configured = _options is not null && _database is not null && await ApiIsUpAsync().ConfigureAwait(false);

        if (!Configured)
        {
            return;
        }

        try
        {
            Created = await RetryAsync(RunCreateScenarioAsync, "the create-a-matter turn").ConfigureAwait(false);
            Gated = await RetryAsync(RunGatedScenarioAsync, "the confirmation-gated delete").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetupFailure = ex;
        }
    }

    public Task DisposeAsync()
    {
        _api.Dispose();
        return Task.CompletedTask;
    }

    // ---- scenario A: a matter is created ------------------------------------

    /// <summary>
    /// One ask that must produce exactly one matter, at a time the user stated
    /// explicitly in a zone that is not UTC.
    ///
    /// <para>
    /// <b>The date is explicit rather than "tomorrow" on purpose.</b> A relative word
    /// makes the expected instant depend on when the suite happens to run, and a turn
    /// straddling local midnight would fail for a reason that is not a defect. An
    /// explicit date still exercises the whole offset path: with an offset-free
    /// <c>currentDate</c> the agent invents <c>+00:00</c> and stores 15:00Z where
    /// 12:00Z belongs.
    /// </para>
    /// </summary>
    private async Task<CreatedMatterTurn> RunCreateScenarioAsync()
    {
        var user = await SignUpAsync().ConfigureAwait(false);

        var cairo = TimeZoneInfo.FindSystemTimeZoneById(CairoZone);
        var target = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, cairo).Date.AddDays(21);
        var stated = $"{target.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} at 3pm";

        var before = await OpenMattersAsync(user).ConfigureAwait(false);

        var events = await AskAsync(user, $"remind me to call the dentist on {stated}").ConfigureAwait(false);

        var after = await OpenMattersAsync(user).ConfigureAwait(false);

        return new CreatedMatterTurn(
            events,
            after.Count - before.Count,
            after.DueAtById,
            TimeZoneInfo.ConvertTimeToUtc(
                new DateTime(target.Year, target.Month, target.Day, 15, 0, 0, DateTimeKind.Unspecified),
                cairo),
            $"{stated}, {CairoZone}");
    }

    // ---- scenario B: a wipe is staged, then confirmed -----------------------

    /// <summary>
    /// Two seeded matters in one domain, a request to wipe that domain, and then the
    /// confirmation — the only path in the product where the model asks before it
    /// destroys anything.
    ///
    /// <para>The confirm goes over HTTP because the route IS the thing being checked:
    /// it reads the pending record this process staged, re-validates the args, runs
    /// the bulk delete and emits the only <c>tool_result</c> the call ever gets.</para>
    /// </summary>
    private async Task<GatedDeleteTurn> RunGatedScenarioAsync()
    {
        var user = await SignUpAsync().ConfigureAwait(false);

        await SeedMatterAsync(user, "rotate the winter tyres").ConfigureAwait(false);
        await SeedMatterAsync(user, "book the annual service").ConfigureAwait(false);

        var before = await OpenMattersAsync(user).ConfigureAwait(false);

        var askEvents = await AskAsync(user, $"delete all my {SeededDomain} tasks").ConfigureAwait(false);

        var afterAsk = await OpenMattersAsync(user).ConfigureAwait(false);
        var statusesAfterAsk = await StoredStatusesAsync(user).ConfigureAwait(false);

        var pending = askEvents
            .Where(e => e.Kind == AiStreamEvents.ToolCallKind
                        && e.Payload.TryGetValue("needsConfirmation", out var flag)
                        && flag is true)
            .Select(e => e.Payload.TryGetValue("callId", out var id) ? id as string : null)
            .FirstOrDefault(id => !string.IsNullOrEmpty(id));

        if (pending is not { Length: > 0 } callId)
        {
            // No card was staged, so there is nothing to confirm. Returned rather than
            // thrown: the frame-level invariant reports this far better than an
            // exception from the harness would.
            return new GatedDeleteTurn(
                askEvents, statusesAfterAsk, before.Count, afterAsk.Count,
                Array.Empty<AiStreamEvent>(), null, statusesAfterAsk, afterAsk.Count);
        }

        // Diagnostic note for whoever sees this scenario go red intermittently: LOOK AT
        // THE AGENT SESSION BEFORE YOU LOOK AT THE FLOW. Every scenario here signs up a
        // fresh account, so no run can inherit another run's memory — but this is the
        // one place a session is deliberately REUSED, because production reuses it too.
        // The confirm re-enters the same session_id with a synthetic "the user confirmed
        // X and it succeeded" prompt, so a degraded model can answer from its memory of
        // the ask turn instead of from the outcome it was just handed. The row counts
        // below are what catch that, and they are why this asserts on the store rather
        // than on the continuation's prose. A reply textually identical to the previous
        // assistant turn is the giveaway.
        var (confirmEvents, confirmFailure) = await ConfirmAsync(user, callId).ConfigureAwait(false);

        return new GatedDeleteTurn(
            askEvents,
            statusesAfterAsk,
            before.Count,
            afterAsk.Count,
            confirmEvents,
            confirmFailure,
            await StoredStatusesAsync(user).ConfigureAwait(false),
            (await OpenMattersAsync(user).ConfigureAwait(false)).Count);
    }

    // ---- the live turn -------------------------------------------------------

    /// <summary>
    /// One real ask, in process, against the configured Langflow.
    ///
    /// <para>Spaced rather than hammered: on the free tier two turns inside fifteen
    /// seconds is reliably a 429, which arrives as an <c>error</c> frame inside a
    /// healthy 200 and would otherwise read as a flow defect.</para>
    /// </summary>
    private async Task<IReadOnlyList<AiStreamEvent>> AskAsync(ScratchUser user, string question)
    {
        await SpaceTurnsAsync().ConfigureAwait(false);

        var provider = new LangflowAiProvider(
            new SmokeHttpClientFactory(),
            _options!,
            SmokeBinding(),
            new AiConversationRepository(_database!),
            new AiGroundingRepository(_database!));

        var request = new AiAskRequest(user.Id, question, CairoZone) { AccessToken = user.AccessToken };

        var events = new List<AiStreamEvent>();
        await foreach (var value in provider.AskAsync(request).ConfigureAwait(false))
        {
            events.Add(value);
        }

        return events;
    }

    /// <summary>
    /// <c>POST /ai/tools/confirm/{callId}</c>, parsed back out of SSE.
    ///
    /// <para>The call id contains a <c>#</c> — <c>{messageId}#{index}</c>, minted
    /// because Langflow's <c>tool_use</c> blocks carry no id of their own — so it MUST
    /// be escaped or the server sees a fragment and never receives it.</para>
    /// </summary>
    private async Task<(IReadOnlyList<AiStreamEvent> Events, string? Failure)> ConfirmAsync(
        ScratchUser user,
        string callId)
    {
        await SpaceTurnsAsync().ConfigureAwait(false);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri($"{ApiBaseUrl}/ai/tools/confirm/{Uri.EscapeDataString(callId)}"))
        {
            Content = new StringContent("""{"action":"confirm"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {user.AccessToken}");

        using var response = await _api.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // A refusal here is EVIDENCE, not a harness error, and asserting on it was a
        // real mistake: with the stream's gating removed, this route answers
        // 404 pending_call_not_found — the record was flipped to `executed` by
        // Langflow's dry-run preview, so the card the user is looking at can never be
        // pressed. That is the whole defect, and throwing from the harness reported it
        // as "the live turns could not be taken" on all six tests instead of letting
        // the gating invariant say what happened. Measured, then fixed.
        return response.IsSuccessStatusCode
            ? (ParseSse(body), null)
            : (Array.Empty<AiStreamEvent>(), $"{(int)response.StatusCode} {body}");
    }

    /// <summary>
    /// SSE back into the seven-event contract, so the confirm stream reaches exactly
    /// the same invariants the in-process turns do. Values arrive as
    /// <see cref="JsonElement"/>s rather than CLR types — which is why the invariants
    /// read their payloads tolerantly.
    /// </summary>
    private static IReadOnlyList<AiStreamEvent> ParseSse(string body)
    {
        var events = new List<AiStreamEvent>();

        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var frame = JsonDocument.Parse(line["data:".Length..].Trim()).RootElement.Clone();
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
            string? kind = null;

            foreach (var property in frame.EnumerateObject())
            {
                if (property.Name == "type")
                {
                    kind = property.Value.GetString();
                    continue;
                }

                payload[property.Name] = property.Value;
            }

            if (kind is not null)
            {
                events.Add(new AiStreamEvent(kind, payload));
            }
        }

        return events;
    }

    private async Task SpaceTurnsAsync()
    {
        var wait = TimeSpan.FromSeconds(SpacingSeconds) - (DateTimeOffset.UtcNow - _lastTurnAt);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait).ConfigureAwait(false);
        }

        _lastTurnAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Re-run a scenario that did not get far enough to assert on — a 429 that landed
    /// before the agent reached its tool, most often. Each attempt starts from a fresh
    /// account, so a half-run attempt is abandoned rather than compounded.
    /// </summary>
    private async Task<T> RetryAsync<T>(Func<Task<T>> scenario, string what)
        where T : ILiveTurn
    {
        var attempts = Setting("LANGFLOW_SMOKE_ATTEMPTS", DefaultAttempts);
        T last = default!;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            last = await scenario().ConfigureAwait(false);

            if (last.Usable)
            {
                return last;
            }
        }

        // Returned, not thrown: the invariants describe what is missing far better
        // than the harness can, and a diagnosis written here would be a guess.
        return last;
    }

    // ---- the API -------------------------------------------------------------

    private const string CairoZone = "Africa/Cairo";

    private const string SeededDomain = "car";

    private async Task<bool> ApiIsUpAsync()
    {
        try
        {
            using var response = await _api.GetAsync(new Uri($"{ApiBaseUrl}/health")).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// A throwaway account, and the self-check that the suite is looking at the same
    /// database the API writes to.
    ///
    /// <para>Without that check a wrong <c>LANGFLOW_SMOKE_MONGO_DB</c> is silent and
    /// baffling: the tools succeed against the API, the conversation is staged
    /// somewhere else, and the confirm route 404s a call it can see no record of.</para>
    /// </summary>
    private async Task<ScratchUser> SignUpAsync()
    {
        var email = $"smoke-{Guid.NewGuid():N}@kitto.test";

        using var response = await _api
            .PostAsJsonAsync(
                new Uri($"{ApiBaseUrl}/auth/signup"),
                new { email, password = ScratchPassword, name = "Langflow smoke" })
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.IsSuccessStatusCode,
            $"POST /auth/signup answered {(int)response.StatusCode}: {body}. The auth limiter allows 20 "
            + "signups per 15 minutes per process — restart the backend if this suite has been re-run.");

        var root = JsonDocument.Parse(body).RootElement;
        var user = new ScratchUser(
            root.GetProperty("user").GetProperty("id").GetString()!,
            root.GetProperty("tokens").GetProperty("accessToken").GetString()!);

        var stored = await _database!
            .GetCollection<BsonDocument>("users")
            .Find(Builders<BsonDocument>.Filter.Eq("_id", ObjectId.Parse(user.Id)))
            .FirstOrDefaultAsync()
            .ConfigureAwait(false);

        Assert.True(
            stored is not null,
            $"the API created a user this suite cannot see in '{DatabaseName}'. The two must be the same "
            + "database or nothing here can be asserted — set LANGFLOW_SMOKE_MONGO_DB to whatever "
            + $"MongoDbSettings__DatabaseName the backend on {ApiBaseUrl} was started with.");

        return user;
    }

    private async Task SeedMatterAsync(ScratchUser user, string title)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{ApiBaseUrl}/me/tasks"))
        {
            Content = JsonContent.Create(new { title, domain = SeededDomain, kind = "list" }),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {user.AccessToken}");

        using var response = await _api.SendAsync(request).ConfigureAwait(false);

        Assert.True(
            response.IsSuccessStatusCode,
            $"seeding a matter answered {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    /// <summary>
    /// The user's open matters, straight off <c>GET /me/tasks</c> — the same view the
    /// app renders, and never anything the model said.
    /// </summary>
    private async Task<OpenMatters> OpenMattersAsync(ScratchUser user)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{ApiBaseUrl}/me/tasks?status=open&limit=100"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {user.AccessToken}");

        using var response = await _api.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        Assert.True(
            response.IsSuccessStatusCode,
            $"GET /me/tasks answered {(int)response.StatusCode}: {body}");

        var tasks = JsonDocument.Parse(body).RootElement.GetProperty("tasks");
        var dueAt = new Dictionary<string, DateTime?>(StringComparer.Ordinal);

        foreach (var task in tasks.EnumerateArray())
        {
            dueAt[task.GetProperty("id").GetString()!] =
                task.TryGetProperty("dueAt", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetDateTimeOffset().UtcDateTime
                    : null;
        }

        return new OpenMatters(dueAt.Count, dueAt);
    }

    /// <summary>
    /// Every tool call the conversation has recorded for this user, by call id. This
    /// is the DURABLE record the confirm route reads, which is the whole reason a
    /// status is worth asserting at all.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> StoredStatusesAsync(ScratchUser user)
    {
        var conversation = await new AiConversationRepository(_database!)
            .LoadAsync(ObjectId.Parse(user.Id), AiConversationVocabulary.PersonalScope, null)
            .ConfigureAwait(false);

        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var call in conversation.Messages.SelectMany(m => m.ToolCalls ?? new List<AiConversationToolCallDocument>()))
        {
            statuses[call.CallId] = call.Status;
        }

        return statuses;
    }

    // ---- opt-in configuration ------------------------------------------------

    /// <summary>
    /// Reads the SMOKE-prefixed variables into the adapter's own keys, so pointing
    /// this at an instance cannot accidentally reconfigure anything else.
    /// </summary>
    public static IConfiguration SmokeConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Langflow:BaseUrl"] = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_BASE_URL"),
                ["Ai:Langflow:FlowId"] = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_FLOW_ID"),
                ["Ai:Langflow:ApiKey"] = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_API_KEY"),
                ["Ai:Langflow:InputNode"] = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_INPUT_NODE"),
            })
            .Build();

    public static LangflowOptions? SmokeOptions()
    {
        var options = LangflowOptions.FromConfiguration(SmokeConfiguration());
        return options.IsConfigured ? options : null;
    }

    private static LangflowInputBinding SmokeBinding() =>
        LangflowInputBinding.FromConfiguration(SmokeConfiguration());

    public static string ApiBaseUrl =>
        (Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_API_BASE_URL") ?? DefaultApiBaseUrl).TrimEnd('/');

    private static string DatabaseName =>
        Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_MONGO_DB") ?? DefaultDatabase;

    private static int SpacingSeconds => Setting("LANGFLOW_SMOKE_SPACING_SECONDS", DefaultSpacingSeconds);

    private static int Setting(string name, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(name),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
        && value >= 0
            ? value
            : fallback;

    /// <summary>
    /// The database the RUNNING backend writes to, not the parity scratch one — the
    /// flow's tools reach the API, so that is where a created matter lands, and the
    /// confirm route has to find the pending record this process staged.
    /// </summary>
    public static IMongoDatabase? TryGetDatabase()
    {
        try
        {
            // MUST precede the first collection resolution, or the class map is built
            // with PascalCase element names and every hand-written update path misses.
            MongoKernelConventions.Register();

            var uri = Environment.GetEnvironmentVariable("LANGFLOW_SMOKE_MONGO_URI")
                      ?? KernelWebApplicationFactory.ParityMongoUri;

            var client = new MongoClient($"{uri}/?serverSelectionTimeoutMS=800");
            var database = client.GetDatabase(DatabaseName);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed class SmokeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private readonly record struct ScratchUser(string Id, string AccessToken);

    private readonly record struct OpenMatters(int Count, IReadOnlyDictionary<string, DateTime?> DueAtById);
}

/// <summary>Lets the harness's retry loop ask a scenario "did this get far enough?".</summary>
public interface ILiveTurn
{
    bool Usable { get; }
}

/// <summary>
/// One real turn that was asked to create a matter, plus everything measured about the
/// store either side of it. Nothing here is derived from the reply.
/// </summary>
public sealed record CreatedMatterTurn(
    IReadOnlyList<AiStreamEvent> Events,
    int MattersCreated,
    IReadOnlyDictionary<string, DateTime?> StoredDueAtById,
    DateTime ExpectedDueAtUtc,
    string StatedLocally) : ILiveTurn
{
    public static readonly CreatedMatterTurn Empty = new(
        Array.Empty<AiStreamEvent>(),
        0,
        new Dictionary<string, DateTime?>(),
        default,
        string.Empty);

    /// <summary>
    /// <b>Every claim this turn is asked to support must be present, or it is retried
    /// on a fresh account.</b>
    ///
    /// <para>
    /// Learned the hard way: a free-tier 429 can land AFTER the agent's tool has
    /// already run. The matter exists, a <c>tool_call</c> was announced, and the stream
    /// then stops with an <c>error</c> frame and no result and no prose. Accepting that
    /// as usable makes the tool-result and envelope assertions fail for a reason that
    /// is not a defect — the exact flakiness these tests are supposed to avoid.
    /// </para>
    /// </summary>
    public bool Usable =>
        MattersCreated > 0
        && Events.Any(e => e.Kind == AiStreamEvents.ToolCallKind)
        && Events.Any(e => e.Kind == AiStreamEvents.ToolResultKind)
        && Events.Any(e => e.Kind == AiStreamEvents.TokenKind);
}

/// <summary>
/// The staged wipe and its confirmation. <c>OpenAfterAsk</c> is the one that proves
/// the dry run really was dry; <c>OpenAfterConfirm</c> is the one that proves the gate
/// opens.
/// </summary>
public sealed record GatedDeleteTurn(
    IReadOnlyList<AiStreamEvent> AskEvents,
    IReadOnlyDictionary<string, string> StatusesAfterAsk,
    int OpenBeforeAsk,
    int OpenAfterAsk,
    IReadOnlyList<AiStreamEvent> ConfirmEvents,
    string? ConfirmFailure,
    IReadOnlyDictionary<string, string> StatusesAfterConfirm,
    int OpenAfterConfirm) : ILiveTurn
{
    public static readonly GatedDeleteTurn Empty = new(
        Array.Empty<AiStreamEvent>(),
        new Dictionary<string, string>(),
        0,
        0,
        Array.Empty<AiStreamEvent>(),
        null,
        new Dictionary<string, string>(),
        0);

    /// <summary>
    /// A confirm that was REFUSED is a finished, informative scenario, not a flake —
    /// retrying a 404 <c>pending_call_not_found</c> three times would burn model calls
    /// to reach the same deterministic answer. Only a scenario that never reached the
    /// confirm at all (the ask 429'd before staging a card) is worth another account.
    /// </summary>
    public bool Usable => ConfirmEvents.Count > 0 || ConfirmFailure is not null;
}
