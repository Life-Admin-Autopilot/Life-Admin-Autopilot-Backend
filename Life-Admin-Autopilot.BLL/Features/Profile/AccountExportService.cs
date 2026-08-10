using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Life_Admin_Autopilot.BLL.Kernel.Json;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.DAL.Features.Profile;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;

namespace Life_Admin_Autopilot.BLL.Features.Profile;

/// <summary>
/// Builds the <c>GET /me/export</c> document — everything the account holds, as
/// one JSON file.
///
/// <para>
/// Two rules govern the contents, both ported verbatim from
/// <c>routes/me.export.ts</c>. Nothing that is a CREDENTIAL ships:
/// no <c>passwordHash</c> (dropped by the user mapper), no refresh
/// <c>tokenHash</c>, and no verification tokens at all — there is no section for
/// them. And nothing that is a BLOB ships: voice audio and scanned originals stay
/// in storage, so their <c>storageKey</c> is projected out; their metadata and
/// extracted text do ship, which is the part that is actually about the user.
/// </para>
/// </summary>
public interface IAccountExportService
{
    /// <summary>The rendered file body — pretty-printed JSON text, ready to send.</summary>
    Task<string> BuildAsync(UserProfileDocument user, DateTime now, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountExportService"/>
public sealed class AccountExportService : IAccountExportService
{
    /// <summary>
    /// <c>MAX_PER_COLLECTION</c>. A generous ceiling that still bounds the response:
    /// a heavier account gets a truncated export rather than a request that dies
    /// halfway.
    /// </summary>
    public const int MaxPerCollection = 5_000;

    /// <summary>
    /// The eleven sections, in the reference's declaration order — which is also
    /// the order they appear in the file.
    /// </summary>
    private static readonly IReadOnlyList<ExportSection> Sections = new[]
    {
        new ExportSection("matters", MongoCollections.Tasks),
        new ExportSection("bulkOperations", MongoCollections.TaskBulkOps),
        new ExportSection("voiceNotes", MongoCollections.VoiceNotes, "storageKey"),
        new ExportSection("documents", MongoCollections.ScannedDocuments, "storageKey"),
        new ExportSection("conversations", MongoCollections.AiConversations),
        new ExportSection("clarifications", MongoCollections.Clarifications),
        new ExportSection("dailyDigests", MongoCollections.DailyDigests),
        new ExportSection("notifications", MongoCollections.Notifications),
        new ExportSection("aiUsage", MongoCollections.AiUsageCounters),
        new ExportSection("documentScanUsage", MongoCollections.DocumentScanUsageCounters),
        new ExportSection("sessions", MongoCollections.RefreshTokens, "tokenHash", "replacedBy"),
    };

    /// <summary>
    /// <c>JSON.stringify(payload, null, 2)</c>. Two-space indentation is
    /// System.Text.Json's default when <c>WriteIndented</c> is on, so the only
    /// thing to pin is the encoder — the default would escape non-ASCII into
    /// <c>\uXXXX</c> and turn every Arabic matter title into an escape sequence.
    /// </summary>
    private static readonly JsonSerializerOptions PrettyText = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IAccountExportRepository _repository;

    public AccountExportService(IAccountExportRepository repository)
    {
        _repository = repository;
    }

    public async Task<string> BuildAsync(
        UserProfileDocument user,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["exportedAt"] = JsIsoDateTimeConverter.ToIso(now),
            ["version"] = 1,

            // The ONLY toJSON'd section: passwordHash dropped, _id renamed to id,
            // __v gone, hasPassword derived. Everything below it is a raw document.
            ["user"] = JsonSerializer.SerializeToNode(user.ToDto(), UserDtoJson.Options),
        };

        foreach (var section in Sections)
        {
            var rows = await _repository
                .FindRawAsync(section.Collection, user.Id, MaxPerCollection, section.ExcludedFields, cancellationToken)
                .ConfigureAwait(false);

            payload[section.Key] = BuildSection(rows);
        }

        return payload.ToJsonString(PrettyText);
    }

    /// <summary>
    /// <c>{ count, truncated, items }</c>.
    ///
    /// <para>
    /// <b><c>truncated</c> is <c>items.length === 5000</c> exactly.</b> That is a
    /// false NEGATIVE for an account with 5001 rows dropped to 5000 — no, worse: it
    /// reports TRUE for an account holding exactly 5000 rows even though nothing was
    /// dropped, and there is no way to distinguish the two because the query has no
    /// sort and no pagination. Known reference bug; replicated deliberately.
    /// </para>
    /// </summary>
    private static JsonObject BuildSection(IReadOnlyList<MongoDB.Bson.BsonDocument> rows)
    {
        var items = new JsonArray();
        foreach (var row in rows)
        {
            items.Add(LeanDocumentJson.ToJsonObject(row));
        }

        return new JsonObject
        {
            ["count"] = rows.Count,
            ["truncated"] = rows.Count == MaxPerCollection,
            ["items"] = items,
        };
    }

    private readonly record struct ExportSection(
        string Key,
        string Collection,
        IReadOnlyList<string>? ExcludedFields)
    {
        public ExportSection(string key, string collection)
            : this(key, collection, null)
        {
        }

        public ExportSection(string key, string collection, params string[] excludedFields)
            : this(key, collection, (IReadOnlyList<string>)excludedFields)
        {
        }
    }
}

/// <summary>
/// Serializer options for the one <c>toJSON</c>'d section.
///
/// <para>
/// The kernel's own options live in the PL project, which the BLL cannot
/// reference, so the settings that matter to a DTO are restated here: camelCase
/// names, unset optionals omitted (Mongoose never stores them, so
/// <c>renewsAt</c>/<c>canceledAt</c> must be ABSENT keys rather than
/// <c>null</c>), the relaxed encoder, and the three-fractional-digit date format.
/// </para>
/// </summary>
internal static class UserDtoJson
{
    internal static readonly JsonSerializerOptions Options = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.Converters.Add(new JsIsoDateTimeConverter());
        options.Converters.Add(new JsIsoNullableDateTimeConverter());
        return options;
    }
}
