using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Features.IcsFeeds.Binding;

namespace Life_Admin_Autopilot.Tests.Features.IcsFeeds;

/// <summary>
/// <c>invalid_feed</c> is the ONE validation error in the whole API whose
/// <c>details</c> is the raw zod issues ARRAY rather than the dot-joined
/// <c>{path,message}</c> list or the <c>.flatten()</c> object.
///
/// <para>
/// Every expectation below was captured from the live reference on port 4200. The
/// per-code member sets are the fragile part: <c>too_small</c> carries
/// <c>minimum/type/inclusive/exact</c> and <c>invalid_enum_value</c> carries
/// <c>options</c>, none of which survive a projection through the kernel's generic
/// <c>ValidationIssue</c>.
/// </para>
/// </summary>
public sealed class IcsSubscribeValidatorTests
{
    [Fact]
    public void accepts_a_well_formed_body_and_trims_only_the_label()
    {
        // `label` is .trim().min(1).max(80); `url` has no trim in the schema — the URL
        // guard trims it later, and the distinction is observable in what gets stored.
        var input = IcsSubscribeValidator.Validate(
            Body(@"{""url"":"" https://x.example/a.ics "",""label"":""  Term dates  "",""domain"":""family""}"));

        Assert.Equal(" https://x.example/a.ics ", input.Url);
        Assert.Equal("Term dates", input.Label);
        Assert.Equal("family", input.Domain);
    }

    [Fact]
    public void ignores_unknown_keys()
    {
        // The Node schema is a plain z.object, not .strict() — unknown keys are
        // stripped, not rejected.
        var input = IcsSubscribeValidator.Validate(
            Body(@"{""url"":""https://x.example/a.ics"",""label"":""L"",""domain"":""pets"",""bogus"":1}"));

        Assert.Equal("pets", input.Domain);
    }

    [Fact]
    public void reports_a_missing_body_as_three_required_issues_in_schema_order()
    {
        var details = Details(Body("{}"));

        Assert.Equal(3, details.GetArrayLength());
        AssertIssue(details[0], code: "invalid_type", path: "url", message: "Required");
        AssertIssue(details[1], code: "invalid_type", path: "label", message: "Required");
        AssertIssue(details[2], code: "invalid_type", path: "domain", message: "Required");

        Assert.Equal("string", details[0].GetProperty("expected").GetString());
        Assert.Equal("undefined", details[0].GetProperty("received").GetString());

        // The enum's `expected` is the quoted union, not the word "string".
        Assert.Equal(
            "'health' | 'home' | 'car' | 'finance' | 'family' | 'pets'",
            details[2].GetProperty("expected").GetString());
    }

    [Fact]
    public void reports_an_empty_url_as_too_small_with_every_zod_member()
    {
        var details = Details(Body(@"{""url"":"""",""label"":""x"",""domain"":""family""}"));

        var issue = Assert.Single(details.EnumerateArray());
        Assert.Equal("too_small", issue.GetProperty("code").GetString());
        Assert.Equal(1, issue.GetProperty("minimum").GetInt32());
        Assert.Equal("string", issue.GetProperty("type").GetString());
        Assert.True(issue.GetProperty("inclusive").GetBoolean());
        Assert.False(issue.GetProperty("exact").GetBoolean());
        Assert.Equal("String must contain at least 1 character(s)", issue.GetProperty("message").GetString());
        Assert.Equal(new[] { "url" }, issue.GetProperty("path").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void reports_an_over_long_url_as_too_big()
    {
        var details = Details(Body(
            $@"{{""url"":""{new string('h', 2049)}"",""label"":""x"",""domain"":""family""}}"));

        var issue = Assert.Single(details.EnumerateArray());
        Assert.Equal("too_big", issue.GetProperty("code").GetString());
        Assert.Equal(2048, issue.GetProperty("maximum").GetInt32());
        Assert.Equal("String must contain at most 2048 character(s)", issue.GetProperty("message").GetString());
    }

    [Fact]
    public void treats_a_whitespace_only_label_as_too_small()
    {
        // .trim() sits EARLIER in the chain than .min(1), so "   " fails rather than
        // being stored as "". Verified live.
        var details = Details(Body(@"{""url"":""https://x.example/a.ics"",""label"":""   "",""domain"":""family""}"));

        var issue = Assert.Single(details.EnumerateArray());
        Assert.Equal("too_small", issue.GetProperty("code").GetString());
        Assert.Equal(new[] { "label" }, issue.GetProperty("path").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void reports_an_unknown_domain_as_invalid_enum_value_with_its_options()
    {
        var details = Details(Body(@"{""url"":""https://x.example/a.ics"",""label"":""x"",""domain"":""nope""}"));

        var issue = Assert.Single(details.EnumerateArray());
        Assert.Equal("invalid_enum_value", issue.GetProperty("code").GetString());
        Assert.Equal("nope", issue.GetProperty("received").GetString());
        Assert.Equal(
            new[] { "health", "home", "car", "finance", "family", "pets" },
            issue.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(
            "Invalid enum value. Expected 'health' | 'home' | 'car' | 'finance' | 'family' | 'pets', received 'nope'",
            issue.GetProperty("message").GetString());
    }

    [Fact]
    public void names_the_received_json_type_the_way_zod_does()
    {
        var details = Details(Body(@"{""url"":5,""label"":true,""domain"":[]}"));

        Assert.Equal("Expected string, received number", details[0].GetProperty("message").GetString());
        Assert.Equal("Expected string, received boolean", details[1].GetProperty("message").GetString());
        Assert.Equal(
            "Expected 'health' | 'home' | 'car' | 'finance' | 'family' | 'pets', received array",
            details[2].GetProperty("message").GetString());
    }

    [Fact]
    public void treats_an_explicit_null_as_a_type_error_not_a_missing_field()
    {
        var details = Details(Body(@"{""url"":null,""label"":""x"",""domain"":""family""}"));

        var issue = Assert.Single(details.EnumerateArray());
        Assert.Equal("null", issue.GetProperty("received").GetString());
        Assert.Equal("Expected string, received null", issue.GetProperty("message").GetString());
    }

    [Fact]
    public void carries_the_routes_own_code_and_message()
    {
        var error = Assert.Throws<AppException>(() => IcsSubscribeValidator.Validate(Body("{}")));

        Assert.Equal(400, error.Status);
        Assert.Equal("invalid_feed", error.Code);
        Assert.Equal("Check the address, name and category.", error.Message);
    }

    // ---- helpers -----------------------------------------------------------

    private static IcsSubscribeBody Body(string json) =>
        JsonSerializer.Deserialize<IcsSubscribeBody>(json)!;

    /// <summary>
    /// Round-trips the details through the serializer, because the shape that matters
    /// is the one on the wire — a member the mapper drops would still be present on
    /// the CLR object.
    /// </summary>
    private static JsonElement Details(IcsSubscribeBody body)
    {
        var error = Assert.Throws<AppException>(() => IcsSubscribeValidator.Validate(body));
        var json = JsonSerializer.Serialize(error.Details);

        return JsonDocument.Parse(json).RootElement;
    }

    private static void AssertIssue(JsonElement issue, string code, string path, string message)
    {
        Assert.Equal(code, issue.GetProperty("code").GetString());
        Assert.Equal(message, issue.GetProperty("message").GetString());
        Assert.Equal(new[] { path }, issue.GetProperty("path").EnumerateArray().Select(p => p.GetString()));
    }
}
