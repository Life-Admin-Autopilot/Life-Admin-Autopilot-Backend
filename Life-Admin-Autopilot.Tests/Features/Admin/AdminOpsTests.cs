using System.Net;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using Life_Admin_Autopilot.DAL.Kernel.Ops;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>Kill switches, the activation funnel, and the CSV export.</summary>
[Collection("admin-serial")]
public sealed class AdminOpsTests : IClassFixture<AdminWebApplicationFactory>
{
    private readonly AdminWebApplicationFactory _factory;

    public AdminOpsTests(AdminWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task ResetAsync()
    {
        await AdminTestData.ClearAsync(
            _factory.Database(),
            "users", "adminfeatureflags", "adminauditevents", "tasks",
            "scanneddocuments", "aiconversations", "voicenotes");
    }

    // ---- kill switches -----------------------------------------------------

    /// <summary>
    /// Every known switch is listed even before anyone has touched one, and the
    /// default is RUNNING. A flag subsystem whose default was "off" would take the
    /// product down on first deploy.
    /// </summary>
    [Fact]
    public async Task lists_every_switch_as_running_by_default()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/ops/flags"));

        var flags = json.EnumerateArray().ToList();

        Assert.Equal(FeatureFlags.All.Count, flags.Count);
        Assert.All(flags, f => Assert.False(f.GetProperty("disabled").GetBoolean()));

        foreach (var key in FeatureFlags.All)
        {
            Assert.Contains(flags, f => f.GetProperty("key").GetString() == key);
        }
    }

    [Fact]
    public async Task turning_a_switch_off_persists_it_with_the_reason_and_the_actor()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/ops/flags/{FeatureFlags.AiChat}",
            AdminTestData.Json(new { disabled = true, reason = "Gemini billing cap reached" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _factory.Database()
            .GetCollection<BsonDocument>(OpsCollections.FeatureFlags)
            .Find(new BsonDocument("key", FeatureFlags.AiChat))
            .FirstAsync();

        Assert.True(stored["disabled"].AsBoolean);
        Assert.Equal("Gemini billing cap reached", stored["reason"].AsString);
        Assert.Equal("admin@test.local", stored["updatedBy"].AsString);
    }

    /// <summary>
    /// The read-back must reflect the write immediately. The store caches for ten
    /// seconds; if the cache were not evicted on write, the operator who just
    /// pulled a switch would see it still running and pull it again.
    /// </summary>
    [Fact]
    public async Task the_switch_reads_back_immediately_after_being_flipped()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var client = _factory.AdminClient();

        await client.PostAsync(
            $"/admin/ops/flags/{FeatureFlags.DocumentScan}",
            AdminTestData.Json(new { disabled = true, reason = "Extraction regressed overnight" }));

        var json = await AdminTestData.ReadAsync(await client.GetAsync("/admin/ops/flags"));

        var scan = json.EnumerateArray()
            .Single(f => f.GetProperty("key").GetString() == FeatureFlags.DocumentScan);

        Assert.True(scan.GetProperty("disabled").GetBoolean());
    }

    [Fact]
    public async Task turning_a_switch_back_on_clears_it()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var client = _factory.AdminClient();
        var path = $"/admin/ops/flags/{FeatureFlags.Transcription}";

        await client.PostAsync(path, AdminTestData.Json(new { disabled = true, reason = "Provider outage" }));
        await client.PostAsync(path, AdminTestData.Json(new { disabled = false, reason = "Provider recovered" }));

        var json = await AdminTestData.ReadAsync(await client.GetAsync("/admin/ops/flags"));

        var flag = json.EnumerateArray()
            .Single(f => f.GetProperty("key").GetString() == FeatureFlags.Transcription);

        Assert.False(flag.GetProperty("disabled").GetBoolean());
    }

    [Fact]
    public async Task refuses_an_unknown_switch()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var response = await _factory.AdminClient().PostAsync(
            "/admin/ops/flags/not_a_real_switch",
            AdminTestData.Json(new { disabled = true, reason = "Testing validation" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await AdminTestData.ReadAsync(response);
        Assert.Equal("unknown_flag", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task refuses_to_flip_a_switch_without_a_reason()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var response = await _factory.AdminClient().PostAsync(
            $"/admin/ops/flags/{FeatureFlags.AiChat}",
            AdminTestData.Json(new { disabled = true, reason = "" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(
            0,
            await _factory.Database()
                .GetCollection<BsonDocument>(OpsCollections.FeatureFlags)
                .CountDocumentsAsync(new BsonDocument()));
    }

    [Fact]
    public async Task flipping_a_switch_is_audited()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        await _factory.AdminClient().PostAsync(
            $"/admin/ops/flags/{FeatureFlags.AiChat}",
            AdminTestData.Json(new { disabled = true, reason = "Cost spike overnight" }));

        var entry = await _factory.Database()
            .GetCollection<BsonDocument>("adminauditevents")
            .Find(new BsonDocument("action", AdminAuditAction.FeatureToggled))
            .FirstAsync();

        Assert.Equal("Cost spike overnight", entry["reason"].AsString);
        Assert.Equal(FeatureFlags.AiChat, entry["details"]["flag"].AsString);
        Assert.True(entry["details"]["disabled"].AsBoolean);
    }

    // ---- funnel ------------------------------------------------------------

    /// <summary>
    /// <b>The funnel must never widen.</b> Each rung counts users who have EVER
    /// reached it, so a later step exceeding an earlier one is arithmetically
    /// impossible — and if it ever happens, the page is lying about where people
    /// drop.
    /// </summary>
    [Fact]
    public async Task the_funnel_never_widens()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var db = _factory.Database();

        // Ten signups; six verified; four onboarded; two with a matter — and the
        // sets are NESTED here on purpose. That is what made this test pass against
        // the broken implementation, which is why
        // `the_funnel_does_not_widen_when_the_steps_are_not_nested` exists below.
        for (var i = 0; i < 10; i++)
        {
            var index = i;
            await AdminTestData.SeedUserAsync(db, $"funnel{i}@test.local", doc =>
            {
                doc["hasOnboarded"] = index < 4;
                if (index < 6)
                {
                    doc["emailVerifiedAt"] = DateTime.UtcNow;
                }
            });
        }

        var everyone = await db.GetCollection<BsonDocument>("users").Find(new BsonDocument()).ToListAsync();
        for (var i = 0; i < 2; i++)
        {
            await db.GetCollection<BsonDocument>("tasks").InsertOneAsync(new BsonDocument
            {
                ["userId"] = everyone[i]["_id"],
                ["title"] = "a matter",
                ["createdAt"] = DateTime.UtcNow,
            });
        }

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/funnel"));

        var steps = json.EnumerateArray().Select(s => s.GetProperty("users").GetInt32()).ToList();

        Assert.Equal(10, steps[0]);
        Assert.Equal(6, steps[1]);
        Assert.Equal(4, steps[2]);
        Assert.Equal(2, steps[3]);

        for (var i = 1; i < steps.Count; i++)
        {
            Assert.True(
                steps[i] <= steps[i - 1],
                $"step {i} ({steps[i]}) is wider than step {i - 1} ({steps[i - 1]})");
        }
    }

    /// <summary>
    /// <b>The case that exposed the original bug.</b>
    ///
    /// <para>
    /// Nobody is onboarded, yet two people have created a matter — which is
    /// entirely possible in the real product. The first implementation counted each
    /// rung independently and reported "Onboarded: 0 → Created a matter: 2", a
    /// funnel that widens. The earlier monotonicity test passed only because it
    /// happened to seed nested data.
    /// </para>
    /// </summary>
    [Fact]
    public async Task the_funnel_does_not_widen_when_the_steps_are_not_nested()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var db = _factory.Database();

        // Six signups, three verified, NOBODY onboarded.
        for (var i = 0; i < 6; i++)
        {
            var index = i;
            await AdminTestData.SeedUserAsync(db, $"nonnested{i}@test.local", doc =>
            {
                doc["hasOnboarded"] = false;
                if (index < 3)
                {
                    doc["emailVerifiedAt"] = DateTime.UtcNow;
                }
            });
        }

        // Two of them create a matter anyway.
        var everyone = await db.GetCollection<BsonDocument>("users").Find(new BsonDocument()).ToListAsync();
        for (var i = 0; i < 2; i++)
        {
            await db.GetCollection<BsonDocument>("tasks").InsertOneAsync(new BsonDocument
            {
                ["userId"] = everyone[i]["_id"],
                ["title"] = "a matter created without onboarding",
                ["createdAt"] = DateTime.UtcNow,
            });
        }

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/funnel"));

        var steps = json.EnumerateArray().Select(s => s.GetProperty("users").GetInt32()).ToList();

        Assert.Equal(6, steps[0]); // signed up
        Assert.Equal(3, steps[1]); // verified
        Assert.Equal(0, steps[2]); // onboarded

        // The rung AFTER an empty one must also be empty — a funnel cannot refill.
        Assert.Equal(0, steps[3]);

        for (var i = 1; i < steps.Count; i++)
        {
            Assert.True(steps[i] <= steps[i - 1], $"step {i} widened: {steps[i]} > {steps[i - 1]}");
        }
    }

    /// <summary>
    /// Adoption is reported as independent counts, so it is allowed to exceed a
    /// funnel rung. Chat, scan and voice are parallel features and stacking them
    /// into a sequence would invent a progression the product does not have.
    /// </summary>
    [Fact]
    public async Task adoption_counts_features_independently()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var db = _factory.Database();
        var alice = await AdminTestData.SeedUserAsync(db, "adopter@test.local");
        var bob = await AdminTestData.SeedUserAsync(db, "scanner@test.local");

        await db.GetCollection<BsonDocument>("aiconversations")
            .InsertOneAsync(new BsonDocument { ["userId"] = alice, ["scope"] = "personal" });

        await db.GetCollection<BsonDocument>("scanneddocuments")
            .InsertManyAsync(new[]
            {
                new BsonDocument { ["userId"] = alice },
                new BsonDocument { ["userId"] = bob },
            });

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/adoption"));

        var rows = json.EnumerateArray().ToList();

        var scans = rows.Single(r => r.GetProperty("step").GetString() == "Scanned a document");
        var chat = rows.Single(r => r.GetProperty("step").GetString() == "Used AI chat");

        // Two distinct scanners, not two scans.
        Assert.Equal(2, scans.GetProperty("users").GetInt32());
        Assert.Equal(1, chat.GetProperty("users").GetInt32());

        // Sorted most-adopted first.
        Assert.Equal(rows.Select(r => r.GetProperty("users").GetInt32()).OrderByDescending(n => n),
            rows.Select(r => r.GetProperty("users").GetInt32()));
    }

    [Fact]
    public async Task the_funnel_is_all_zeroes_on_an_empty_database()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();

        var json = await AdminTestData.ReadAsync(
            await _factory.AdminClient().GetAsync("/admin/insights/funnel"));

        // Every percentage divides by the cohort size. Zero customers must not
        // produce a divide-by-zero or a NaN in the JSON.
        Assert.All(json.EnumerateArray(), s =>
        {
            Assert.Equal(0, s.GetProperty("users").GetInt32());
            Assert.Equal(0, s.GetProperty("percentOfCohort").GetDouble());
        });
    }

    // ---- CSV export --------------------------------------------------------

    /// <summary>
    /// <b>The formula-injection guard.</b>
    ///
    /// <para>
    /// A field beginning <c>=</c>, <c>+</c>, <c>-</c> or <c>@</c> is executed by
    /// Excel and Sheets when the file is opened. An email address is user-chosen,
    /// so without this an attacker picks an address that runs a formula on the
    /// machine of whoever exports the customer list.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("=cmd|'/c calc'!A1@evil.test")]
    [InlineData("+1234@evil.test")]
    [InlineData("-2+3@evil.test")]
    [InlineData("@SUM(1:1)@evil.test")]
    public void csv_neutralises_a_formula_in_a_user_controlled_field(string dangerous)
    {
        var csv = AdminOpsService.ToCsv(new[]
        {
            new AdminCustomerRowDto { Id = "abc", Email = dangerous, Tier = "free" },
        });

        var emailCell = csv.Split('\n')[1].Split("\",\"")[1];

        Assert.StartsWith("\t", emailCell);
        Assert.DoesNotContain($"\"{dangerous}\"", csv);
    }

    /// <summary>A quote inside a value must not break out of its cell.</summary>
    [Fact]
    public void csv_escapes_embedded_quotes()
    {
        var csv = AdminOpsService.ToCsv(new[]
        {
            new AdminCustomerRowDto
            {
                Id = "abc",
                Email = "someone@test.local",
                DisplayName = "Ali \"The Closer\" Hassan",
                Tier = "free",
            },
        });

        Assert.Contains("\"Ali \"\"The Closer\"\" Hassan\"", csv);

        // Header plus exactly one data row — an unescaped quote would have split it.
        Assert.Equal(2, csv.TrimEnd('\n').Split('\n').Length);
    }

    /// <summary>A comma in a name must not create a phantom column.</summary>
    [Fact]
    public void csv_survives_a_comma_in_a_value()
    {
        var csv = AdminOpsService.ToCsv(new[]
        {
            new AdminCustomerRowDto
            {
                Id = "abc",
                Email = "someone@test.local",
                DisplayName = "Hassan, Ali",
                Tier = "free",
            },
        });

        var header = csv.Split('\n')[0].Split(',').Length;
        var row = csv.Split('\n')[1].Split("\",\"").Length;

        Assert.Equal(header, row);
    }

    [Fact]
    public async Task exporting_is_audited_before_a_byte_is_produced()
    {
        if (!_factory.MongoIsUp()) return;
        await ResetAsync();
        await AdminTestData.SeedUserAsync(_factory.Database(), "exported@test.local");

        var response = await _factory.AdminClient().GetAsync("/admin/customers/export?segment=all");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("exported@test.local", body);

        Assert.Equal(
            1,
            await _factory.Database()
                .GetCollection<BsonDocument>("adminauditevents")
                .CountDocumentsAsync(new BsonDocument("action", AdminAuditAction.CustomerExported)));
    }
}
