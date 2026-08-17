using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Kernel.Audit;

/// <summary>Collection this slice owns. Never TTL'd — see <see cref="AdminAuditEventDocument"/>.</summary>
public static class AuditCollections
{
    public const string AdminAuditEvents = "adminauditevents";
}

/// <summary>
/// The closed vocabulary of auditable admin actions.
///
/// <para>
/// Strings are stable and namespaced <c>subject.verb</c> so the console can filter
/// on a prefix. Adding a mutating endpoint means adding a constant here — if an
/// action is not in this list it should not be reachable.
/// </para>
/// </summary>
public static class AdminAuditAction
{
    // --- Reads that are themselves sensitive -------------------------------
    /// <summary>Opened one customer's detail page. Cheap to record, and the only way to answer "who looked at this account?"</summary>
    public const string CustomerViewed = "customer.viewed";

    /// <summary>Exported a filtered customer list. The classic insider-risk event.</summary>
    public const string CustomerExported = "customer.exported";

    // --- Account state -----------------------------------------------------
    public const string CustomerSuspended = "customer.suspended";
    public const string CustomerRestored = "customer.restored";
    public const string CustomerQuotaReset = "customer.quota_reset";
    public const string CustomerTierGranted = "customer.tier_granted";
    public const string CustomerPasswordResetForced = "customer.password_reset_forced";
    public const string CustomerSessionsRevoked = "customer.sessions_revoked";
    public const string CustomerVerificationResent = "customer.verification_resent";
    public const string CustomerDeleted = "customer.deleted";

    /// <summary>An admin sent this customer a push / in-app message. Cannot be recalled.</summary>
    public const string CustomerNotified = "customer.notified";

    // --- Operations --------------------------------------------------------

    /// <summary>A message to a whole segment. The highest-blast-radius action here.</summary>
    public const string Broadcast = "ops.broadcast";

    public const string FeatureToggled = "ops.feature_toggled";
    public const string AdminInvited = "ops.admin_invited";
    public const string AdminRoleChanged = "ops.admin_role_changed";
    public const string AdminRevoked = "ops.admin_revoked";
}

/// <summary>Whether the attempt actually did anything.</summary>
public static class AdminAuditOutcome
{
    public const string Ok = "ok";

    /// <summary>Authorization refused it. Recorded precisely because it is the interesting one.</summary>
    public const string Denied = "denied";

    /// <summary>Authorized, attempted, and blew up.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One thing an admin did.
///
/// <para>
/// <b>Append-only, and never TTL'd.</b> There is no update path and no delete path
/// on the store, because an audit log an application can edit is not evidence of
/// anything. The 36-month retention floor that privileged-action logging is
/// normally held to is implemented as "we never delete these" — simpler, and it
/// cannot drift.
/// </para>
///
/// <para>
/// <b>Actor and target identities are denormalised.</b> An audit row has to stay
/// readable after the admin leaves and after the customer deletes their account —
/// which is exactly when someone will be reading it. A row that resolves to two
/// dangling ids answers nothing.
/// </para>
/// </summary>
public sealed class AdminAuditEventDocument
{
    [BsonId]
    [BsonIgnoreIfDefault]
    public ObjectId Id { get; set; }

    public DateTime At { get; set; }

    /// <summary>Identity id of the admin who acted.</summary>
    public Guid ActorId { get; set; }

    /// <summary>Denormalised — see the type summary.</summary>
    public string ActorEmail { get; set; } = string.Empty;

    /// <summary>Which role the actor held at the time. Roles change; this must not.</summary>
    public string ActorRole { get; set; } = string.Empty;

    /// <summary>One of <see cref="AdminAuditAction"/>.</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Mongo id of the affected customer, when there is one.</summary>
    public string? TargetUserId { get; set; }

    /// <summary>Denormalised — see the type summary.</summary>
    public string? TargetEmail { get; set; }

    /// <summary>
    /// Typed by the admin, and <b>required on every mutating action</b>.
    ///
    /// <para>
    /// Not decoration. A reason box is the cheapest control that exists against
    /// casual misuse: it converts an idle click into a deliberate, attributable
    /// statement, and it is the field that makes the log readable a year later.
    /// </para>
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>Whatever else the action needs to be reconstructible — old and new values.</summary>
    public BsonDocument? Details { get; set; }

    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>One of <see cref="AdminAuditOutcome"/>.</summary>
    public string Outcome { get; set; } = AdminAuditOutcome.Ok;

    /// <summary>Set when <see cref="Outcome"/> is not <c>ok</c>.</summary>
    public string? Error { get; set; }
}
