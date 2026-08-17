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
    /// Activation, as counts of distinct users who ever reached each step.
    ///
    /// <para>
    /// <b>Cumulative-ever, not per-cohort-window.</b> Each rung counts users who
    /// have EVER done the thing, so the steps are monotonically non-increasing and
    /// the drop between two rungs is real. A windowed version would let a later
    /// rung exceed an earlier one — a user who onboarded last year and scanned a
    /// document today — which reads as a bug even when the data is right.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<FunnelStepDto>> FunnelAsync(CancellationToken cancellationToken = default)
    {
        var total = await _customers.TotalCustomersAsync(cancellationToken).ConfigureAwait(false);

        var users = _database.GetCollection<BsonDocument>(MongoCollections.Users);
        var tasks = _database.GetCollection<BsonDocument>(MongoCollections.Tasks);
        var scans = _database.GetCollection<BsonDocument>(MongoCollections.ScannedDocuments);
        var conversations = _database.GetCollection<BsonDocument>(MongoCollections.AiConversations);
        var voice = _database.GetCollection<BsonDocument>(MongoCollections.VoiceNotes);

        var verified = await users
            .CountDocumentsAsync(
                new BsonDocument("emailVerifiedAt", new BsonDocument("$ne", BsonNull.Value)),
                options: null,
                cancellationToken)
            .ConfigureAwait(false);

        var onboarded = await users
            .CountDocumentsAsync(new BsonDocument("hasOnboarded", true), options: null, cancellationToken)
            .ConfigureAwait(false);

        var withMatter = await DistinctUserCountAsync(tasks, cancellationToken).ConfigureAwait(false);
        var withChat = await DistinctUserCountAsync(conversations, cancellationToken).ConfigureAwait(false);
        var withScan = await DistinctUserCountAsync(scans, cancellationToken).ConfigureAwait(false);
        var withVoice = await DistinctUserCountAsync(voice, cancellationToken).ConfigureAwait(false);

        var steps = new (string Step, long Users)[]
        {
            ("Signed up", total),
            ("Verified email", verified),
            ("Onboarded", onboarded),
            ("First matter", withMatter),
            ("First AI turn", withChat),
            ("First scan", withScan),
            ("First voice note", withVoice),
        };

        return steps
            .Select(s => new FunnelStepDto
            {
                Step = s.Step,
                Users = (int)s.Users,
                PercentOfCohort = total == 0 ? 0 : Math.Round(s.Users * 100.0 / total, 1),
            })
            .ToList();
    }

    /// <summary>
    /// How many distinct users appear in a collection.
    ///
    /// <para>
    /// <c>$group</c> then <c>$count</c> rather than <c>Distinct</c>: the distinct
    /// command materialises every id into one BSON document and hits the 16MB cap
    /// somewhere north of half a million users, which is a failure that only shows
    /// up once the product is working.
    /// </para>
    /// </summary>
    private static async Task<long> DistinctUserCountAsync(
        IMongoCollection<BsonDocument> collection,
        CancellationToken cancellationToken)
    {
        var pipeline = new[]
        {
            new BsonDocument("$group", new BsonDocument("_id", "$userId")),
            new BsonDocument("$count", "n"),
        };

        var result = await collection
            .Aggregate<BsonDocument>(pipeline, cancellationToken: cancellationToken)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return result?.GetValue("n", 0).ToInt64() ?? 0;
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
