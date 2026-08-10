using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Features.IcsFeeds;

/// <summary>Closed vocabulary from <c>server/src/models/IcsFeed.ts</c>.</summary>
public static class IcsFeedVocabulary
{
    /// <summary><c>gone</c> is TERMINAL — a 404/410 does not start working again on its own.</summary>
    public static readonly IReadOnlyList<string> Statuses = new[] { "active", "error", "gone" };

    public const string Active = "active";
    public const string Error = "error";
    public const string Gone = "gone";

    /// <summary>
    /// <c>Task.externalSource</c> for everything an ICS subscription produces.
    /// Shared namespace with the Google slice; a matter is identified by the
    /// (userId, externalSource, externalId) triple.
    /// </summary>
    public const string ExternalSource = "ics_feed";

    public const int MaxUrlLength = 2048;
    public const int MaxLabelLength = 80;
}

/// <summary>
/// Port of <c>server/src/models/IcsFeed.ts</c> — a calendar feed the user
/// subscribed to (school terms, bin collections, sports fixtures).
///
/// <para>
/// Read-only and public: an ICS URL is a bearer capability, so it is never treated
/// as a credential and never sent anywhere.
/// </para>
///
/// <para>
/// Feeds are stored PER USER rather than deduplicated globally on purpose. Two
/// parents at the same school hold the same URL, but they may file it under
/// different domains and their timezones differ; sharing a row would couple their
/// sync failures for no real saving.
/// </para>
/// </summary>
public sealed class IcsFeedDocument
{
    [BsonId]
    public ObjectId Id { get; set; }

    /// <summary>
    /// Mongoose stamps <c>__v: 0</c> on insert for every model except User, which
    /// is the only one setting <c>versionKey: false</c>
    /// (<c>server/src/models/User.ts:217</c>). The .NET driver adds nothing, so a
    /// document written here was missing a field the reference stores. Observable
    /// today through <c>GET /me/export</c>, which returns raw stored rows.
    /// </summary>
    [BsonElement("__v")]
    public int SchemaVersion { get; set; }

    public ObjectId UserId { get; set; }

    /// <summary>
    /// Normalised absolute http(s) URL — <c>webcal:</c> is rewritten before it lands
    /// here, and the value is the output of the URL parser's own <c>toString()</c>,
    /// so it can differ from what the client sent. The (userId, url) uniqueness
    /// check runs on THIS form.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>What the user calls it. Shown wherever its events are cited.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Which life domain this feed's events file into.</summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Publisher cache validators from the last successful fetch, replayed on the
    /// next poll. This is the whole reason polling an ICS feed is affordable: a
    /// term-dates feed changes about three times a year, so nearly every poll ends
    /// at a 304 with no body, no parsing and no writes.
    ///
    /// <para><b>Stripped by <c>toJSON</c></b> — see the DTO mapper. They are
    /// plumbing, mean nothing to a client, and only invite someone to try to "fix" a
    /// stuck sync by editing them.</para>
    /// </summary>
    public string? Etag { get; set; }

    public string? LastModified { get; set; }

    /// <summary>Every attempt, successful or not.</summary>
    public DateTime? LastFetchedAt { get; set; }

    /// <summary>Only attempts that actually returned a body we parsed.</summary>
    public DateTime? LastSyncedAt { get; set; }

    public string Status { get; set; } = IcsFeedVocabulary.Active;

    /// <summary>
    /// Plain-language reason the last poll failed. Surfaced to the user, because a
    /// feed that silently stops updating looks identical to a feed with nothing on
    /// it — and the user would go on trusting reminders that stopped arriving.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>Consecutive failures. Drives the poller's backoff so a dead URL is not hammered.</summary>
    public int FailureCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
