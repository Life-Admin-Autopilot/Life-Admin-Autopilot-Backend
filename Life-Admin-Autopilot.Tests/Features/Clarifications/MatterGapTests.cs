using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Clarifications;

/// <summary>
/// The gap check behind <c>POST /me/tasks</c> — "save it, then ask about what is
/// missing".
///
/// <para>
/// <b>What went wrong without it.</b> The chat agent is told to hold any matter that
/// arrives with no date. Measured live on 2026-08-22 it held "اشتري لبن" every time
/// and "تعلم سباحة" never — it reads the second as an aspiration rather than a to-do
/// and reasons past the instruction. The matter was filed silently, with no date and
/// no question, so it could never resurface and never appeared in Needs You either.
/// No rewording moved it, which is why the rule now lives on the server.
/// </para>
///
/// <para>
/// The questions themselves are <see cref="VoiceAutoFilePolicy"/>'s, called through
/// <c>GapsFor</c>. That is deliberate rather than convenient: two lanes asking a user
/// the same thing in two different sentences is a bug avoided by construction here,
/// not by two implementations kept in step.
/// </para>
/// </summary>
public sealed class MatterGapTests : IClassFixture<ClarificationsWebApplicationFactory>
{
    private readonly ClarificationsWebApplicationFactory _factory;

    public MatterGapTests(ClarificationsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---- the policy, with no database in the way ----------------------------

    [Fact]
    public void no_date_asks_when_it_is_due()
    {
        var gaps = VoiceAutoFilePolicy.GapsFor(Draft(dueAt: null), "Africa/Cairo", timeAssumed: false);

        var gap = Assert.Single(gaps);
        Assert.Equal("ask.whenDue", gap.QuestionKey);
        Assert.Equal("date", gap.Kind);

        // Option ZERO carries no date, and that is load-bearing: the matter was
        // filed undated, and anything else in that slot would date a matter the
        // user never dated while the card claimed to be asking them.
        Assert.Null(gap.Options[0].DueAt);
        Assert.Equal("chip.noDateNeeded", gap.Options[0].LabelKey);

        // The rest carry real instants AND a key, so the chip reads in the user's
        // language and the time is formatted by their own locale.
        Assert.All(gap.Options.Skip(1), o =>
        {
            Assert.NotNull(o.DueAt);
            Assert.NotNull(o.LabelKey);
        });
    }

    [Fact]
    public void a_day_with_an_invented_hour_asks_the_hour()
    {
        var at = new DateTime(2026, 8, 23, 6, 0, 0, DateTimeKind.Utc); // 09:00 Cairo
        var gaps = VoiceAutoFilePolicy.GapsFor(Draft(dueAt: at), "Africa/Cairo", timeAssumed: true);

        var gap = Assert.Single(gaps);
        Assert.Equal("ask.whatTimeOn", gap.QuestionKey);

        // Option zero is the time it was ACTUALLY filed under, so "keep this" leaves
        // the matter alone rather than silently moving it.
        Assert.Equal(at, gap.Options[0].DueAt);
    }

    [Fact]
    public void an_hour_the_user_gave_is_not_questioned()
    {
        var at = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

        Assert.Empty(VoiceAutoFilePolicy.GapsFor(Draft(dueAt: at), "Africa/Cairo", timeAssumed: false));
    }

    /// <summary>
    /// A renewal costs money whatever domain it is filed under, which is the case
    /// the domain test alone was missing: "هجدد رخصة العربية" is a CAR matter.
    /// </summary>
    [Theory]
    [InlineData("تجديد رخصة العربية", "car")]
    [InlineData("Renew the car insurance", "car")]
    [InlineData("دفع فاتورة المياه", "home")]
    [InlineData("Pay the rent", "home")]
    public void a_money_matter_is_asked_its_figure_whatever_its_domain(string title, string domain)
    {
        var at = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var gaps = VoiceAutoFilePolicy.GapsFor(
            Draft(dueAt: at, title: title, domain: domain), "Africa/Cairo", timeAssumed: false);

        var gap = Assert.Single(gaps);
        Assert.Equal("ask.howMuch", gap.QuestionKey);

        // No chips: an amount is not a short list, and offering one would be the app
        // inventing what a licence costs.
        Assert.Empty(gap.Options);
    }

    [Fact]
    public void a_figure_already_known_is_not_asked_for_again()
    {
        var at = new DateTime(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);
        var draft = Draft(dueAt: at, title: "دفع فاتورة المياه", domain: "finance") with
        {
            Amount = new MoneyDocument { AmountMinor = 23400, Currency = "EGP" },
        };

        Assert.Empty(VoiceAutoFilePolicy.GapsFor(draft, "Africa/Cairo", timeAssumed: false));
    }

    /// <summary>
    /// BOTH gaps, where the voice lane would ask one. A renewal with no date and no
    /// figure is missing two different things, and the chat card stack answers them
    /// independently — see the note on <c>GapsFor</c>.
    /// </summary>
    [Fact]
    public void a_renewal_with_no_date_is_asked_both()
    {
        var gaps = VoiceAutoFilePolicy.GapsFor(
            Draft(dueAt: null, title: "تجديد رخصة العربية", domain: "car"),
            "Africa/Cairo",
            timeAssumed: false);

        Assert.Equal(new[] { "ask.whenDue", "ask.howMuch" }, gaps.Select(g => g.QuestionKey).ToArray());
    }

    // ---- the route ----------------------------------------------------------

    [Fact]
    public async Task a_dateless_matter_is_saved_AND_asked_about()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var userId = await ResetAsync(db);

        var created = await PostTaskAsync(userId, """
        {
          "title": "تعلم سباحة",
          "domain": "health",
          "askAboutGaps": true,
          "timezone": "Africa/Cairo"
        }
        """);

        // SAVED first. This is the whole point of the ordering: the matter exists
        // whatever the question does next.
        var task = created.GetProperty("task");
        Assert.Equal("تعلم سباحة", task.GetProperty("title").GetString());
        Assert.Equal("list", task.GetProperty("kind").GetString());

        var raised = Assert.Single(created.GetProperty("clarifications").EnumerateArray().ToList());
        Assert.Equal("ask.whenDue", raised.GetProperty("questionKey").GetString());
        Assert.Equal(
            task.GetProperty("id").GetString(),
            raised.GetProperty("taskId").GetString());

        // And it is on the surface Needs You counts, which is the one the user
        // reaches when they miss the card in the conversation.
        var listed = await GetJsonAsync(userId, "/me/clarifications");
        Assert.Equal(
            raised.GetProperty("id").GetString(),
            listed.GetProperty("clarifications")[0].GetProperty("id").GetString());
    }

    /// <summary>
    /// The app's own Add-a-matter sheet, which shows the user a date field. Leaving
    /// it empty there is a choice they took in front of the control, and asking about
    /// it afterwards would be the app arguing with what it just watched them do.
    /// </summary>
    [Fact]
    public async Task the_manual_create_path_asks_nothing()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var userId = await ResetAsync(db);

        var created = await PostTaskAsync(userId, """
        {"title": "تعلم سباحة", "domain": "health"}
        """);

        // The key is absent entirely, not an empty array: the response is
        // byte-for-byte the one this route has always returned.
        Assert.False(created.TryGetProperty("clarifications", out _));
        Assert.Empty(await OpenRowsAsync(db, userId));
    }

    [Fact]
    public async Task a_complete_matter_is_asked_nothing()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var userId = await ResetAsync(db);

        var created = await PostTaskAsync(userId, """
        {
          "title": "حجز كورة",
          "domain": "home",
          "kind": "reminder",
          "dueAt": "2026-08-23T15:00:00.000Z",
          "askAboutGaps": true,
          "timezone": "Africa/Cairo"
        }
        """);

        Assert.False(created.TryGetProperty("clarifications", out _));
        Assert.Empty(await OpenRowsAsync(db, userId));
    }

    /// <summary>
    /// The label and the instant have to agree. Without a zone both are composed in
    /// UTC, so a chip reading "09:00" files 09:00Z — noon in Cairo, a time the user
    /// was never offered. Measured live before the profile fallback existed.
    /// </summary>
    [Fact]
    public async Task the_chips_are_composed_in_the_users_zone()
    {
        var db = TryGetDatabase();
        if (db is null) return;

        var userId = await ResetAsync(db);

        var created = await PostTaskAsync(userId, """
        {
          "title": "تعلم سباحة",
          "domain": "health",
          "askAboutGaps": true,
          "timezone": "Africa/Cairo"
        }
        """);

        var options = created.GetProperty("clarifications")[0].GetProperty("options");

        // "Tomorrow — 09:00", and 09:00 Cairo is 06:00Z. A UTC-composed chip would
        // carry 09:00Z under the same label.
        var tomorrow = options.EnumerateArray()
            .First(o => o.GetProperty("labelKey").GetString() == "chip.tomorrowAt");

        Assert.EndsWith("06:00:00.000Z", tomorrow.GetProperty("dueAt").GetString());
        Assert.Contains("09:00", tomorrow.GetProperty("label").GetString());
    }

    // ---- helpers ------------------------------------------------------------

    private static TaskDraft Draft(
        DateTime? dueAt,
        string title = "تعلم سباحة",
        string domain = "health") =>
        new(
            title,
            domain,
            "normal",
            Kind: dueAt is null ? "list" : "reminder",
            DueAt: dueAt,
            Notes: null,
            SourceType: "chat",
            Confidence: 1,
            Conflicts: Array.Empty<PlanningConflict>());

    private HttpClient AuthedClient(ObjectId userId)
    {
        var client = _factory.CreateApiClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            KernelPipelineTests.NodeShapedToken(userId.ToString(), "m-gap@probe.com"));
        return client;
    }

    private async Task<JsonElement> PostTaskAsync(ObjectId userId, string body)
    {
        var response = await AuthedClient(userId)
            .PostAsync("/me/tasks", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<JsonElement> GetJsonAsync(ObjectId userId, string path)
    {
        var response = await AuthedClient(userId).GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task<List<BsonDocument>> OpenRowsAsync(IMongoDatabase db, ObjectId userId) =>
        await db.GetCollection<BsonDocument>(MongoCollections.Clarifications)
            .Find(Builders<BsonDocument>.Filter.Eq("userId", userId))
            .ToListAsync();

    private static async Task<ObjectId> ResetAsync(IMongoDatabase db)
    {
        var userId = ObjectId.GenerateNewId();
        await db.GetCollection<BsonDocument>(MongoCollections.Clarifications)
            .DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", userId));
        await db.GetCollection<BsonDocument>(MongoCollections.Tasks)
            .DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", userId));
        return userId;
    }

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var database = new MongoClient(settings)
                .GetDatabase(ClarificationsWebApplicationFactory.ClarificationsDatabase);
            database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));

            return database;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
