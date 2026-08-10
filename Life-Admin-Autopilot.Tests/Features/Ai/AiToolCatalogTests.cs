using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Ai;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.Tests.Features.Ai;

/// <summary>
/// The tool vocabulary and the argument re-validation that runs when a pending call
/// is recovered from the durable record.
/// </summary>
public sealed class AiToolCatalogTests
{
    [Fact]
    public void gates_exactly_one_tool()
    {
        // Every per-item mutation runs inline — the agent is trusted with those. The
        // bulk wipe alone keeps a confirmation step, because deleting every matter in
        // one call is the one action an undo toast cannot console anyone about.
        Assert.Equal(
            new[] { "deleteAllTasks" },
            AiToolCatalog.ToolNames.Where(AiToolCatalog.RequiresConfirmation).ToArray());
    }

    [Fact]
    public void keeps_the_eleven_names_the_contract_uses()
    {
        Assert.Equal(11, AiToolCatalog.ToolNames.Count);
        Assert.All(AiToolCatalog.ToolNames, name => Assert.True(AiToolCatalog.IsKnownTool(name)));
        Assert.False(AiToolCatalog.IsKnownTool("dropDatabase"));
    }

    // ---- re-validation on recovery -----------------------------------------

    [Fact]
    public void keeps_a_valid_narrowing()
    {
        var args = AiToolCatalog.ValidateArgs(
            AiToolCatalog.DeleteAllTasks,
            new BsonDocument { ["domain"] = "car", ["status"] = "done" });

        Assert.Equal("car", args["domain"]);
        Assert.Equal("done", args["status"]);
    }

    [Fact]
    public void accepts_the_status_filter_spelling_and_normalises_it()
    {
        // Langflow 1.11.2 refuses a component input named `status` — it collides with
        // Component.status — so the flow puts `status_filter` on the wire. Reading
        // only `status` meant the key was STRIPPED as unknown, and a confirmed
        // "delete all my done tasks" executed as "delete every task", while the card
        // the user agreed to still said "done".
        var args = AiToolCatalog.ValidateArgs(
            AiToolCatalog.DeleteAllTasks,
            new BsonDocument { ["status_filter"] = "done" });

        Assert.Equal("done", args["status"]);
        Assert.False(args.ContainsKey("status_filter"));
    }

    [Fact]
    public void prefers_the_canonical_name_when_a_record_carries_both()
    {
        // A record written across the rename could carry either or both. `status`
        // wins so behaviour does not change under a server that understands both.
        var args = AiToolCatalog.ValidateArgs(
            AiToolCatalog.DeleteAllTasks,
            new BsonDocument { ["status"] = "open", ["status_filter"] = "done" });

        Assert.Equal("open", args["status"]);
    }

    [Fact]
    public void strips_the_display_only_count_the_confirmation_card_carries()
    {
        // The tool_call frame is enriched with a live count so the card reads
        // "Delete all 12 tasks". It must never reach the bulk filter.
        var args = AiToolCatalog.ValidateArgs(
            AiToolCatalog.DeleteAllTasks,
            new BsonDocument { ["domain"] = "home", ["count"] = 12 });

        Assert.False(args.ContainsKey("count"));
        Assert.Single(args);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"domain":null}""")]
    [InlineData("""{"status":null}""")]
    public void treats_an_absent_or_null_narrowing_as_no_narrowing(string json)
    {
        // A stored explicit null is what Mongo hands back for a field the agent chose
        // not to set. Rejecting it would strand every old record.
        Assert.Empty(AiToolCatalog.ValidateArgs(AiToolCatalog.DeleteAllTasks, BsonDocument.Parse(json)));
    }

    [Theory]
    [InlineData("""{"domain":"space"}""", "domain", "Invalid enum value.")]
    [InlineData("""{"status":"pending"}""", "status", "Invalid enum value.")]
    [InlineData("""{"status_filter":"pending"}""", "status_filter", "Invalid enum value.")]
    [InlineData("""{"domain":7}""", "domain", "received number")]
    public void refuses_a_narrowing_it_does_not_recognise(string json, string field, string fragment)
    {
        // This runs BEFORE the stream opens, so a stale record is an ordinary 400
        // rather than an error frame nobody is listening for.
        var error = Assert.Throws<AppException>(() =>
            AiToolCatalog.ValidateArgs(AiToolCatalog.DeleteAllTasks, BsonDocument.Parse(json)));

        Assert.Equal(400, error.Status);
        Assert.Equal("invalid_tool_args", error.Code);

        // Node passes the SERIALIZED zod flatten as the message, not as details.
        var flatten = JsonDocument.Parse(error.Message).RootElement;
        Assert.Empty(flatten.GetProperty("formErrors").EnumerateArray());
        Assert.Contains(fragment, flatten.GetProperty("fieldErrors").GetProperty(field)[0].GetString());
    }

    [Fact]
    public void never_widens_a_delete_by_dropping_a_narrowing_it_could_not_parse()
    {
        // The whole point: an unparseable narrowing must FAIL, never fall through to
        // "no filter" — that is the difference between deleting six tasks and six
        // hundred.
        Assert.ThrowsAny<AppException>(() =>
            AiToolCatalog.ValidateArgs(
                AiToolCatalog.DeleteAllTasks,
                new BsonDocument { ["domain"] = "car", ["status"] = "archived" }));
    }

    [Fact]
    public void passes_a_non_confirmable_tools_args_through_untouched()
    {
        // Only deleteAllTasks can reach a durable pending record, so it is the only
        // schema modelled. Anything else is handed back as-is and refused by the
        // runner's requiresConfirmation gate instead of being half-validated here.
        var args = AiToolCatalog.ValidateArgs("queryTasks", new BsonDocument { ["q"] = "vet" });

        Assert.Equal("vet", args["q"]);
    }

    [Fact]
    public void survives_a_stored_args_value_that_is_not_a_document()
    {
        // `args` is Schema.Types.Mixed, so the storage layer permits any BSON type.
        // Throwing on an oddly-shaped one would turn a confirmation into a 500.
        Assert.Empty(AiToolCatalog.ValidateArgs(AiToolCatalog.DeleteAllTasks, BsonNull.Value));
        Assert.Empty(AiToolCatalog.ValidateArgs(AiToolCatalog.DeleteAllTasks, new BsonString("nonsense")));
    }
}
