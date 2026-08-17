using System.Text;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Kernel.Ops;
using Life_Admin_Autopilot.DAL.Kernel.Telemetry;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Admin;

public sealed class FeatureFlagDto
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("disabled")]
    public bool Disabled { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("updatedBy")]
    public string? UpdatedBy { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; init; }
}

public sealed class ErrorBucketDto
{
    [JsonPropertyName("feature")]
    public string Feature { get; init; } = string.Empty;

    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; init; }
}

/// <summary>One rung of the activation funnel.</summary>
public sealed class FunnelStepDto
{
    [JsonPropertyName("step")]
    public string Step { get; init; } = string.Empty;

    [JsonPropertyName("users")]
    public int Users { get; init; }

    /// <summary>Share of the cohort that reached this step. The number people actually read.</summary>
    [JsonPropertyName("percentOfCohort")]
    public double PercentOfCohort { get; init; }
}

/// <summary>
/// Operations: kill switches, reliability, and the activation funnel.
/// </summary>
public sealed class AdminOpsService
{
    private readonly IFeatureFlagStore _flags;
    private readonly IAiUsageStore _usage;
    private readonly IMongoDatabase _database;
    private readonly IAdminAuditStore _audit;
    private readonly IAdminCustomerRepository _customers;
    private readonly TimeProvider _time;

    public AdminOpsService(
        IFeatureFlagStore flags,
        IAiUsageStore usage,
        IMongoDatabase database,
        IAdminAuditStore audit,
        IAdminCustomerRepository customers,
        TimeProvider? time = null)
    {
        _flags = flags;
        _usage = usage;
        _database = database;
        _audit = audit;
        _customers = customers;
        _time = time ?? TimeProvider.System;
    }

    // ---- flags -------------------------------------------------------------

    public async Task<IReadOnlyList<FeatureFlagDto>> FlagsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _flags.ListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .Select(r => new FeatureFlagDto
            {
                Key = r.Key,
                Disabled = r.Disabled,
                Reason = r.Reason,
                UpdatedBy = r.UpdatedBy,
                UpdatedAt = r.UpdatedAt == default ? null : r.UpdatedAt,
            })
            .ToList();
    }

    public async Task<AdminActionResultDto> SetFlagAsync(
        string key,
        bool disabled,
        string? reason,
        AdminActor actor,
        CancellationToken cancellationToken = default)
    {
        if (!FeatureFlags.IsKnown(key))
        {
            throw AppException.BadRequest(
                "unknown_flag",
                $"'{key}' is not a switch. Known: {string.Join(", ", FeatureFlags.All)}.");
        }

        var justification = reason?.Trim();
        if (string.IsNullOrEmpty(justification) || justification.Length < AdminCustomerService.MinReasonLength)
        {
            throw AppException.BadRequest(
                "reason_required",
                "Turning a capability off for every customer needs a reason.");
        }

        await _audit.AppendAsync(
            new AdminAuditEventDocument
            {
                At = _time.GetUtcNow().UtcDateTime,
                ActorId = actor.Id,
                ActorEmail = actor.Email,
                ActorRole = actor.Role,
                Action = AdminAuditAction.FeatureToggled,
                Reason = justification,
                Ip = actor.Ip,
                UserAgent = actor.UserAgent,
                Details = new BsonDocument { ["flag"] = key, ["disabled"] = disabled },
            },
            cancellationToken).ConfigureAwait(false);

        await _flags.SetAsync(key, disabled, justification, actor.Email, cancellationToken).ConfigureAwait(false);

        return new AdminActionResultDto
        {
            Action = AdminAuditAction.FeatureToggled,
            Message = disabled
                ? $"{key} is now OFF for every customer."
                : $"{key} is back on.",
        };
    }

    // ---- reliability -------------------------------------------------------

    public async Task<IReadOnlyList<ErrorBucketDto>> ErrorsAsync(
        int days,
        CancellationToken cancellationToken = default)
    {
        var window = AdminInsightService.WindowEndingToday(_time.GetUtcNow().UtcDateTime, days);
        var rows = await _usage.ByErrorAsync(window, cancellationToken).ConfigureAwait(false);

        return rows
            .Select(r => new ErrorBucketDto
            {
                Feature = r.Feature,
                ErrorCode = r.ErrorCode,
                Count = r.Count,
                LastSeen = r.LastSeen,
            })
            .ToList();
    }

    // ---- funnel ------------------------------------------------------------

    /// <summary>
    /// Activation, as a TRUE funnel: each rung counts people who reached it
    /// <b>having reached every rung before it</b>.
    ///
    /// <para>
    /// <b>This is a set intersection, not a column of independent counts.</b> The
    /// first version counted each step separately and called the result a funnel,
    /// which produced "Onboarded: 0 → First matter: 2" on real data — a funnel that
    /// widens, which is not a funnel. A customer genuinely can create a matter
    /// without finishing onboarding, so the counts were each correct and the shape
    /// was a lie.
    /// </para>
    ///
    /// <para>
    /// <b>Only the onboarding sequence is a funnel.</b> Chat, document scan and
    /// voice notes are parallel features — nobody does them in a fixed order — so
    /// they are reported separately by <see cref="AdoptionAsync"/> rather than
    /// stacked into a sequence that does not exist.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<FunnelStepDto>> FunnelAsync(CancellationToken cancellationToken = default)
    {
        var users = _database.GetCollection<BsonDocument>(MongoCollections.Users);
        var tasks = _database.GetCollection<BsonDocument>(MongoCollections.Tasks);

        var signedUp = await IdsAsync(
                users,
                new BsonDocument(),
                "_id",
                cancellationToken)
            .ConfigureAwait(false);

        var verified = await IdsAsync(
                users,
                new BsonDocument("emailVerifiedAt", new BsonDocument("$ne", BsonNull.Value)),
                "_id",
                cancellationToken)
            .ConfigureAwait(false);

        var onboarded = await IdsAsync(
                users,
                new BsonDocument("hasOnboarded", true),
                "_id",
                cancellationToken)
            .ConfigureAwait(false);

        var withMatter = await DistinctUserIdsAsync(tasks, cancellationToken).ConfigureAwait(false);

        // Each rung intersects the one before it. That is what makes the drops real.
        var rungs = new List<(string Step, HashSet<BsonValue> Users)>
        {
            ("Signed up", signedUp),
        };

        foreach (var (label, set) in new[]
                 {
                     ("Verified email", verified),
                     ("Onboarded", onboarded),
                     ("Created a matter", withMatter),
                 })
        {
            var previous = rungs[^1].Users;
            rungs.Add((label, new HashSet<BsonValue>(set.Where(previous.Contains))));
        }

        var total = signedUp.Count;

        return rungs
            .Select(r => new FunnelStepDto
            {
                Step = r.Step,
                Users = r.Users.Count,
                PercentOfCohort = total == 0 ? 0 : Math.Round(r.Users.Count * 100.0 / total, 1),
            })
            .ToList();
    }

    /// <summary>
    /// Feature adoption — independent counts, presented as such.
    ///
    /// <para>
    /// These are NOT funnel rungs. A customer may scan a document without ever
    /// using chat, so ordering them into a sequence would invent a progression the
    /// product does not have.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<FunnelStepDto>> AdoptionAsync(CancellationToken cancellationToken = default)
    {
        var total = await _customers.TotalCustomersAsync(cancellationToken).ConfigureAwait(false);

        var sources = new (string Label, string Collection)[]
        {
            ("Used AI chat", MongoCollections.AiConversations),
            ("Scanned a document", MongoCollections.ScannedDocuments),
            ("Recorded a voice note", MongoCollections.VoiceNotes),
        };

        var adoption = new List<FunnelStepDto>();

        foreach (var (label, collection) in sources)
        {
            var ids = await DistinctUserIdsAsync(
                    _database.GetCollection<BsonDocument>(collection),
                    cancellationToken)
                .ConfigureAwait(false);

            adoption.Add(new FunnelStepDto
            {
                Step = label,
                Users = ids.Count,
                PercentOfCohort = total == 0 ? 0 : Math.Round(ids.Count * 100.0 / total, 1),
            });
        }

        return adoption.OrderByDescending(a => a.Users).ToList();
    }

    /// <summary>
    /// Distinct <c>userId</c>s in a collection, as a set.
    ///
    /// <para>
    /// <c>$group</c> rather than the <c>distinct</c> command: distinct materialises
    /// every id into ONE BSON document and hits the 16MB cap somewhere north of half
    /// a million users — a failure that only appears once the product is working.
    /// Bounded by <see cref="AdminCustomerService.MaxSegmentScan"/> for the same
    /// reason a segment is.
    /// </para>
    /// </summary>
    private static Task<HashSet<BsonValue>> DistinctUserIdsAsync(
        IMongoCollection<BsonDocument> collection,
        CancellationToken cancellationToken) =>
        IdsAsync(collection, new BsonDocument(), "$userId", cancellationToken);

    private static async Task<HashSet<BsonValue>> IdsAsync(
        IMongoCollection<BsonDocument> collection,
        BsonDocument match,
        string field,
        CancellationToken cancellationToken)
    {
        var pipeline = new[]
        {
            new BsonDocument("$match", match),
            new BsonDocument("$group", new BsonDocument("_id", field.StartsWith('$') ? field : "$" + field)),
            new BsonDocument("$limit", AdminCustomerService.MaxSegmentScan),
        };

        var rows = await collection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(r => r.GetValue("_id", BsonNull.Value))
            .Where(v => !v.IsBsonNull)
            .ToHashSet();
    }

    // ---- export ------------------------------------------------------------

    /// <summary>
    /// The current customer view as CSV.
    ///
    /// <para>
    /// <b>Every cell is quoted and escaped, including the formula guard.</b> A field
    /// beginning <c>= + - @</c> is executed by Excel and Sheets on open — an email
    /// address a user chose could otherwise run a formula on the machine of whoever
    /// opened the export. Prefixing a tab neutralises it and is invisible in the cell.
    /// </para>
    /// </summary>
    public static string ToCsv(IEnumerable<AdminCustomerRowDto> rows)
    {
        var csv = new StringBuilder();
        csv.AppendLine(
            "id,email,display_name,joined,last_seen,email_verified,suspended,onboarded,tier,locale,"
            + "ai_calls_30d,input_tokens_30d,output_tokens_30d,cost_usd_30d");

        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                Cell(r.Id),
                Cell(r.Email),
                Cell(r.DisplayName),
                Cell(r.CreatedAt.ToString("O")),
                Cell(r.LastActiveAt?.ToString("O")),
                Cell(r.EmailVerified ? "yes" : "no"),
                Cell(r.SuspendedAt is null ? "no" : "yes"),
                Cell(r.HasOnboarded ? "yes" : "no"),
                Cell(r.Tier),
                Cell(r.Locale),
                Cell(r.Usage30d.Calls.ToString()),
                Cell(r.Usage30d.InputTokens.ToString()),
                Cell(r.Usage30d.OutputTokens.ToString()),
                Cell(r.Usage30d.EstimatedCostUsd.ToString("0.######")),
            }));
        }

        return csv.ToString();
    }

    private static string Cell(string? value)
    {
        var text = value ?? string.Empty;

        // The formula guard. See ToCsv.
        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@'))
        {
            text = "\t" + text;
        }

        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
