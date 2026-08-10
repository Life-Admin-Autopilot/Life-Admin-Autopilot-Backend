using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Notifications;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Notifications;

/// <summary>
/// <c>z.object({ids: z.array(z.string()).max(100).optional()}).parse(...)</c>.
///
/// <para>
/// Every expectation here was captured from the live reference at <c>:4200</c>,
/// because this is the API's only <c>.parse()</c> route and the wrong choice
/// among the three <c>details</c> shapes is invisible to a status-code check.
/// </para>
/// </summary>
public sealed class NotificationsReadRequestTests
{
    // ---- The "mark everything" lane ---------------------------------------

    [Fact]
    public void treats_an_empty_object_as_mark_all()
    {
        Assert.Null(Parse("{}"));
    }

    [Fact]
    public void ignores_unknown_keys_because_the_schema_is_not_strict()
    {
        // Verified live: {"zz":1} answers 200, not 400.
        Assert.Null(Parse("""{"zz":1}"""));
    }

    [Fact]
    public void treats_an_empty_ids_array_as_mark_all_not_as_mark_nothing()
    {
        // Node's guard is `ids && ids.length > 0`, so [] never narrows the filter.
        Assert.Null(Parse("""{"ids":[]}"""));
    }

    // ---- The narrowing lane ------------------------------------------------

    [Fact]
    public void keeps_well_formed_object_ids()
    {
        var ids = Parse("""{"ids":["6a78c437aa461ae1dc64ffff"]}""");

        Assert.NotNull(ids);
        Assert.Equal(ObjectId.Parse("6a78c437aa461ae1dc64ffff"), Assert.Single(ids!));
    }

    [Fact]
    public void accepts_uppercase_hex()
    {
        // Verified live: an uppercase id DOES mark its row read.
        var ids = Parse("""{"ids":["6A78C437AA461AE1DC64FFFF"]}""");

        Assert.Equal(ObjectId.Parse("6a78c437aa461ae1dc64ffff"), Assert.Single(ids!));
    }

    [Fact]
    public void silently_drops_malformed_ids_rather_than_rejecting_them()
    {
        var ids = Parse("""{"ids":["6a78c437aa461ae1dc64ffff","not-an-object-id"]}""");

        Assert.Single(ids!);
    }

    [Fact]
    public void drops_a_twelve_character_string_that_mongoose_would_not_match_either()
    {
        // Verified live: {"ids":["abcdefghijkl"]} does NOT mark the notification
        // whose _id holds exactly those bytes. Plain 24-hex parsing is faithful.
        var ids = Parse("""{"ids":["abcdefghijkl"]}""");

        Assert.NotNull(ids);
        Assert.Empty(ids!);
    }

    [Fact]
    public void narrows_to_an_empty_set_when_every_id_was_malformed()
    {
        // NOT null — an all-garbage list is a deliberate no-op, not a mark-all.
        var ids = Parse("""{"ids":["nope"]}""");

        Assert.NotNull(ids);
        Assert.Empty(ids!);
    }

    [Fact]
    public void accepts_exactly_one_hundred_ids()
    {
        var body = $$"""{"ids":[{{string.Join(',', Enumerable.Repeat("\"6a78c437aa461ae1dc64ffff\"", 100))}}]}""";

        Assert.Equal(100, Parse(body)!.Count);
    }

    // ---- The validation_error lane ----------------------------------------

    [Fact]
    public void reports_a_non_string_element_at_its_indexed_path()
    {
        var issues = Issues("""{"ids":[1,2]}""");

        Assert.Collection(
            issues,
            i => AssertIssue(i, "ids.0", "Expected string, received number"),
            i => AssertIssue(i, "ids.1", "Expected string, received number"));
    }

    [Fact]
    public void names_every_received_type_the_way_zod_does()
    {
        var issues = Issues("""{"ids":[1,"ok",true,null,{},[]]}""");

        Assert.Collection(
            issues,
            i => AssertIssue(i, "ids.0", "Expected string, received number"),
            i => AssertIssue(i, "ids.2", "Expected string, received boolean"),
            i => AssertIssue(i, "ids.3", "Expected string, received null"),
            i => AssertIssue(i, "ids.4", "Expected string, received object"),
            i => AssertIssue(i, "ids.5", "Expected string, received array"));
    }

    [Fact]
    public void reports_a_non_array_ids_at_the_field_path()
    {
        AssertIssue(Assert.Single(Issues("""{"ids":"foo"}""")), "ids", "Expected array, received string");
    }

    [Fact]
    public void treats_an_explicit_null_as_a_type_error_not_an_absent_key()
    {
        // JSON has no `undefined`, so `.optional()` never sees this as missing.
        AssertIssue(Assert.Single(Issues("""{"ids":null}""")), "ids", "Expected array, received null");
    }

    [Fact]
    public void rejects_more_than_one_hundred_ids()
    {
        var body = $$"""{"ids":[{{string.Join(',', Enumerable.Repeat("\"a\"", 101))}}]}""";

        AssertIssue(Assert.Single(Issues(body)), "ids", "Array must contain at most 100 element(s)");
    }

    [Fact]
    public void emits_the_array_size_issue_before_the_element_issues()
    {
        // The ORDER is observable and is not the obvious one — zod checks the
        // array's own constraints before walking its elements. Verified live.
        var body = $$"""{"ids":[1,{{string.Join(',', Enumerable.Repeat("\"a\"", 100))}}]}""";

        var issues = Issues(body);

        Assert.Collection(
            issues,
            i => AssertIssue(i, "ids", "Array must contain at most 100 element(s)"),
            i => AssertIssue(i, "ids.0", "Expected string, received number"));
    }

    [Fact]
    public void reports_an_array_body_at_the_empty_path()
    {
        // express.json() accepts a top-level array, so zod sees one and reports a
        // whole-object type issue whose dot-joined path is the empty string.
        AssertIssue(Assert.Single(Issues("[1,2]")), string.Empty, "Expected object, received array");
    }

    // ---- helpers -----------------------------------------------------------

    private static IReadOnlyList<ObjectId>? Parse(string json) =>
        NotificationsReadRequest.Parse(JsonDocument.Parse(json).RootElement);

    private static IReadOnlyList<ValidationIssue> Issues(string json) =>
        Assert.Throws<ValidationException>(() => Parse(json)).Issues;

    private static void AssertIssue(ValidationIssue issue, string path, string message)
    {
        Assert.Equal(path, string.Join('.', issue.Path));
        Assert.Equal(message, issue.Message);
    }
}
