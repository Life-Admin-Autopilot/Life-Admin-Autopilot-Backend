using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.Profile.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Json;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Profile;

/// <summary>
/// <c>UpdateMeSchema</c> + <c>buildSet</c>, the two halves of <c>PATCH /me</c>.
///
/// <para>
/// Every expected string here was copied from a live <c>:4200</c> response. These
/// are unit tests over the binder, so they run with no Mongo and no HTTP.
/// </para>
/// </summary>
public sealed class UpdateMeValidatorTests
{
    // ---- The dot-notation flattening ---------------------------------------

    [Fact]
    public void flattens_a_nested_patch_into_dot_notation_so_siblings_survive()
    {
        // Arrange — THE regression this route exists to prevent. Setting the whole
        // `notifications` sub-document would reset emailDigest and marketing to
        // their schema defaults, and the frontend sends one toggle at a time.
        var set = BuildSet("""{"notifications":{"push":false}}""");

        // Assert
        Assert.Equal(new[] { "notifications.push" }, set.Select(p => p.Key).ToArray());
        Assert.Equal(BsonBoolean.False, set[0].Value);
    }

    [Fact]
    public void flattens_every_one_of_the_four_nested_objects()
    {
        // Act
        var set = BuildSet(
            """
            {"mic":{"quality":"high"},
             "notifications":{"push":false,"emailDigest":true,"marketing":true},
             "privacy":{"analytics":false,"crashReports":false},
             "imports":{"defaultTimeOfDay":"07:30"}}
            """);

        // Assert — sub-keys in SCHEMA order within each object, objects in schema
        // order overall.
        Assert.Equal(
            new[]
            {
                "mic.quality",
                "notifications.push",
                "notifications.emailDigest",
                "notifications.marketing",
                "privacy.analytics",
                "privacy.crashReports",
                "imports.defaultTimeOfDay",
            },
            set.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void replaces_arrays_wholesale_rather_than_merging_them()
    {
        // Arrange — preferredDomains and onboardingAnswers are NOT in the nested set,
        // so they are set under their own key and replace the previous value.
        var set = BuildSet(
            """{"preferredDomains":["home","finance"],"onboardingAnswers":[{"id":"a","question":"q","answer":"x"}]}""");

        // Assert
        Assert.Equal(new[] { "preferredDomains", "onboardingAnswers" }, set.Select(p => p.Key).ToArray());
        Assert.Equal(new BsonArray { "home", "finance" }, set[0].Value);
    }

    [Fact]
    public void an_empty_body_produces_an_empty_set()
    {
        // Assert — valid, and a 200 on the reference. The repository still stamps
        // updatedAt, which is what makes it a "touch".
        Assert.Empty(BuildSet("{}"));
    }

    [Fact]
    public void an_empty_nested_object_sets_nothing()
    {
        Assert.Empty(BuildSet("""{"mic":{}}"""));
    }

    [Fact]
    public void strips_unknown_keys_instead_of_rejecting_them()
    {
        // Arrange — the schema is a plain z.object, not .strict(). Verified live:
        // this body is a 200 that changes nothing.
        var set = BuildSet("""{"email":"attacker@example.com","subscription":{"tier":"pro"},"hasPassword":false}""");

        // Assert
        Assert.Empty(set);
    }

    [Fact]
    public void strips_unknown_keys_inside_a_nested_object_too()
    {
        Assert.Empty(BuildSet("""{"mic":{"bogus":1}}"""));
    }

    // ---- The trim-last display name ----------------------------------------

    [Fact]
    public void stores_a_whitespace_only_display_name_as_the_empty_string()
    {
        // Arrange — `.min(1).max(80).trim()`: the LENGTH RUNS FIRST, so "   " is
        // three characters and passes, then the trim empties it. Measured. A port
        // that trims first rejects this body instead.
        var set = BuildSet("""{"displayName":"   "}""");

        // Assert
        Assert.Equal("displayName", set[0].Key);
        Assert.Equal(BsonString.Empty, set[0].Value);
    }

    [Fact]
    public void accepts_an_eighty_character_display_name_and_rejects_eighty_one()
    {
        Assert.Single(BuildSet($$"""{"displayName":"{{new string('a', 80)}}"}"""));
        Assert.Equal(
            "String must contain at most 80 character(s)",
            FieldError($$"""{"displayName":"{{new string('a', 81)}}"}""", "displayName")[0]);
    }

    // ---- Validation messages, all measured ---------------------------------

    [Fact]
    public void reports_the_route_code_and_message_verbatim()
    {
        // Act
        var error = Assert.Throws<AppException>(() => BuildSet("""{"timezone":"Not/AZone"}"""));

        // Assert
        Assert.Equal(400, error.Status);
        Assert.Equal("invalid_body", error.Code);
        Assert.Equal("Some of those settings looked off.", error.Message);
    }

    [Theory]
    [InlineData("""{"timezone":"Not/AZone"}""", "timezone", "Not a recognised time zone.")]
    [InlineData("""{"locale":"not a locale!!"}""", "locale", "Not a recognised locale.")]
    [InlineData("""{"displayName":null}""", "displayName", "Expected string, received null")]
    [InlineData("""{"displayName":""}""", "displayName", "String must contain at least 1 character(s)")]
    [InlineData("""{"hasOnboarded":"true"}""", "hasOnboarded", "Expected boolean, received string")]
    [InlineData("""{"timezone":123}""", "timezone", "Expected string, received number")]
    [InlineData("""{"theme":"neon"}""", "theme", "Invalid enum value. Expected 'system' | 'light' | 'dark', received 'neon'")]
    [InlineData("""{"textSize":"xl"}""", "textSize", "Invalid enum value. Expected 'sm' | 'md' | 'lg', received 'xl'")]
    [InlineData("""{"preferredDomains":"home"}""", "preferredDomains", "Expected array, received string")]
    [InlineData("""{"onboardingAnswers":["x"]}""", "onboardingAnswers", "Expected object, received string")]
    [InlineData("""{"onboardingAnswers":[{"id":"a","question":"q"}]}""", "onboardingAnswers", "Required")]
    public void emits_the_measured_message_for(string body, string field, string expected) =>
        Assert.Equal(expected, Assert.Single(FieldError(body, field)));

    [Theory]
    // A nested issue is keyed under the TOP-LEVEL name, because zod's flatten()
    // groups by path[0]. `mic.quality` therefore reports as `mic`.
    [InlineData("""{"mic":{"quality":"ultra"}}""", "mic", "Invalid enum value. Expected 'standard' | 'high', received 'ultra'")]
    [InlineData("""{"mic":{"quality":null}}""", "mic", "Expected 'standard' | 'high', received null")]
    [InlineData("""{"mic":"x"}""", "mic", "Expected object, received string")]
    [InlineData("""{"mic":[]}""", "mic", "Expected object, received array")]
    [InlineData("""{"notifications":{"push":"yes"}}""", "notifications", "Expected boolean, received string")]
    [InlineData("""{"imports":{"defaultTimeOfDay":"9:00"}}""", "imports", "Use a 24-hour time like 09:00.")]
    [InlineData("""{"imports":{"defaultTimeOfDay":930}}""", "imports", "Expected string, received number")]
    public void keys_a_nested_issue_under_its_top_level_field(string body, string field, string expected) =>
        Assert.Equal(expected, Assert.Single(FieldError(body, field)));

    [Fact]
    public void reports_the_enum_type_failure_and_the_enum_value_failure_differently()
    {
        // Arrange — the split is by TYPE, not by value: a non-string is the
        // invalid_type form, a string that is not a member is invalid_enum_value.
        // Both measured, and a single generic mapper gets one of them wrong.
        Assert.StartsWith("Expected 'health' | ", Assert.Single(FieldError("""{"preferredDomains":[1]}""", "preferredDomains")));
        Assert.StartsWith("Invalid enum value. ", Assert.Single(FieldError("""{"preferredDomains":["nope"]}""", "preferredDomains")));
    }

    [Fact]
    public void runs_the_refinement_even_after_the_length_check_failed()
    {
        // Arrange — a zod string length failure marks the result DIRTY, not
        // ABORTED, and only an abort short-circuits a ZodEffects refinement. So an
        // empty timezone reports BOTH issues, in this order. Measured.
        var messages = FieldError("""{"timezone":""}""", "timezone");

        // Assert
        Assert.Equal(
            new[] { "String must contain at least 1 character(s)", "Not a recognised time zone." },
            messages);
    }

    [Fact]
    public void skips_the_refinement_when_the_type_was_wrong()
    {
        // Arrange — a type failure DOES abort, so there is no second message.
        Assert.Equal("Expected string, received number", Assert.Single(FieldError("""{"locale":123}""", "locale")));
    }

    [Fact]
    public void reports_every_bad_array_member_in_order()
    {
        // Act
        var messages = FieldError("""{"preferredDomains":["nope","alsonope"]}""", "preferredDomains");

        // Assert
        Assert.Equal(2, messages.Count);
        Assert.EndsWith("received 'nope'", messages[0]);
        Assert.EndsWith("received 'alsonope'", messages[1]);
    }

    [Fact]
    public void reports_nested_boolean_issues_in_schema_order_not_body_order()
    {
        // Arrange — zod walks its own shape, so a body ordered
        // {marketing, push, emailDigest} still reports push, emailDigest, marketing.
        // All three messages are identical here; what the test pins is that all
        // three fire and none is dropped.
        var messages = FieldError("""{"notifications":{"marketing":1,"push":2,"emailDigest":3}}""", "notifications");

        // Assert
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.Equal("Expected boolean, received number", m));
    }

    [Fact]
    public void orders_field_keys_by_schema_declaration_not_by_body_order()
    {
        // Arrange — measured: a body of {privacy, mic, notifications} answers with
        // fieldErrors keyed mic, notifications, privacy.
        var details = Flatten("""{"privacy":{"analytics":1},"mic":{"quality":9},"notifications":{"push":"x"}}""");

        // Assert
        Assert.Equal(new[] { "mic", "notifications", "privacy" }, details.FieldErrors.Keys.ToArray());
    }

    [Fact]
    public void caps_onboarding_answers_at_twenty()
    {
        // Arrange
        var items = string.Join(',', Enumerable.Range(0, 21).Select(i => $$"""{"id":"{{i}}","question":"q","answer":"a"}"""));

        // Assert
        Assert.Equal(
            "Array must contain at most 20 element(s)",
            Assert.Single(FieldError($$"""{"onboardingAnswers":[{{items}}]}""", "onboardingAnswers")));
    }

    [Fact]
    public void enforces_the_per_member_length_caps_on_an_onboarding_answer()
    {
        Assert.Equal(
            "String must contain at most 100 character(s)",
            Assert.Single(FieldError(
                $$"""{"onboardingAnswers":[{"id":"{{new string('x', 101)}}","question":"q","answer":"a"}]}""",
                "onboardingAnswers")));
    }

    [Fact]
    public void formErrors_stays_empty_for_every_field_level_failure()
    {
        // Assert — the flatten shape always ships both keys; only fieldErrors fills.
        Assert.Empty(Flatten("""{"theme":"neon"}""").FormErrors);
    }

    // ---- helpers ------------------------------------------------------------

    private static IReadOnlyList<KeyValuePair<string, BsonValue>> BuildSet(string json) =>
        UpdateMeValidator.BuildSet(
            JsonSerializer.Deserialize<UpdateMeBody>(json, KernelJson.Lenient)!);

    private static FlattenedValidationDetails Flatten(string json)
    {
        var error = Assert.Throws<AppException>(() => BuildSet(json));
        return Assert.IsType<FlattenedValidationDetails>(error.Details);
    }

    private static IReadOnlyList<string> FieldError(string json, string field)
    {
        var details = Flatten(json);
        Assert.True(details.FieldErrors.ContainsKey(field), $"no fieldErrors['{field}']");
        return details.FieldErrors[field];
    }
}
