using Life_Admin_Autopilot.DAL.Features.Admin;
using Life_Admin_Autopilot.DAL.Kernel.Audit;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.DAL.Push;
using Life_Admin_Autopilot.DAL.Push.Models;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.Admin;

/// <summary>What an admin is sending.</summary>
public sealed record AdminMessage(string Title, string Body)
{
    /// <summary>Push payloads are truncated by the OS well before this; these are the sane ceilings.</summary>
    public const int MaxTitle = 80;

    public const int MaxBody = 300;

    public AdminMessage Validated()
    {
        var title = Title?.Trim() ?? string.Empty;
        var body = Body?.Trim() ?? string.Empty;

        if (title.Length is 0 or > MaxTitle)
        {
            throw AppException.BadRequest(
                "invalid_title",
                $"A title is required and must be at most {MaxTitle} characters.");
        }

        if (body.Length is 0 or > MaxBody)
        {
            throw AppException.BadRequest(
                "invalid_body",
                $"A body is required and must be at most {MaxBody} characters.");
        }

        return new AdminMessage(title, body);
    }
}

/// <summary>One device's outcome. The masked token is enough to tell two phones apart, and no more.</summary>
public sealed record DeviceDelivery(string Platform, string Token, bool Delivered, string? Error);

/// <summary>What happened when a message was sent to one customer.</summary>
public sealed record NotifyOutcome(
    int DevicesTargeted,
    int Delivered,
    int Failed,
    bool InAppCreated,
    IReadOnlyList<DeviceDelivery> Devices);

/// <summary>A broadcast, per recipient.</summary>
public sealed record BroadcastOutcome(int Recipients, int Delivered, int Failed, int InAppCreated);

/// <summary>
/// Admin-initiated messages to customers.
///
/// <para>
/// <b>The in-app row is the message; push is a doorbell.</b> The notification
/// document is written FIRST and unconditionally, so a customer with no
/// registered device, a revoked push permission, or a dead token still sees what
/// was sent the next time they open the app. Treating push as the message —
/// which is the obvious implementation — silently drops it for exactly the users
/// least likely to be reachable another way.
/// </para>
/// </summary>
public sealed class AdminNotificationService
{
    /// <summary>
    /// The notification kind admin messages use.
    ///
    /// <para>
    /// The app's <c>NotificationBell</c> switches on <c>uncertainty</c> and
    /// <c>document_scan</c> and falls through to a default for everything else, so
    /// an unknown kind renders as title + body rather than breaking. It still
    /// deserves its own name in the ledger: calling an admin message a "reminder"
    /// would make the customer's own history lie about where it came from.
    /// </para>
    /// </summary>
    public const string AnnouncementKind = "announcement";

    /// <summary>
    /// A hard ceiling on one broadcast, independent of the segment's size.
    ///
    /// <para>
    /// Not a paging limit — a blast radius. A mistyped segment that reaches every
    /// customer is the single most damaging thing this console can do, and it is
    /// not undoable. Above this the caller is told the count and refused.
    /// </para>
    /// </summary>
    public const int MaxBroadcastRecipients = 500;

    private readonly IMongoDatabase _database;
    private readonly IDeviceTokenRepository _devices;
    private readonly IPushNotificationService _push;
    private readonly IAdminCustomerRepository _customers;
    private readonly IAdminAuditStore _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<AdminNotificationService> _logger;

    public AdminNotificationService(
        IMongoDatabase database,
        IDeviceTokenRepository devices,
        IPushNotificationService push,
        IAdminCustomerRepository customers,
        IAdminAuditStore audit,
        ILogger<AdminNotificationService> logger,
        TimeProvider? time = null)
    {
        _database = database;
        _devices = devices;
        _push = push;
        _customers = customers;
        _audit = audit;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    private IMongoCollection<NotificationDocument> Notifications =>
        _database.GetCollection<NotificationDocument>(MongoCollections.Notifications);

    /// <summary>Send to one customer, and record it.</summary>
    public async Task<NotifyOutcome> NotifyAsync(
        ObjectId userId,
        AdminMessage message,
        AdminActor actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var validated = message.Validated();
        var justification = RequireReason(reason);

        var user = await _customers.FindAsync(userId, cancellationToken).ConfigureAwait(false)
            ?? throw AppException.NotFound("customer_not_found", "No customer with that id.");

        var at = _time.GetUtcNow().UtcDateTime;

        // Audit BEFORE sending. A push cannot be recalled, so the record of who
        // sent what has to exist before the thing leaves the building.
        await _audit.AppendAsync(
            new AdminAuditEventDocument
            {
                At = at,
                ActorId = actor.Id,
                ActorEmail = actor.Email,
                ActorRole = actor.Role,
                Action = AdminAuditAction.CustomerNotified,
                TargetUserId = userId.ToString(),
                TargetEmail = user.Email,
                Reason = justification,
                Ip = actor.Ip,
                UserAgent = actor.UserAgent,
                Details = new BsonDocument
                {
                    ["title"] = validated.Title,
                    ["body"] = validated.Body,
                },
            },
            cancellationToken).ConfigureAwait(false);

        var outcome = await DeliverAsync(userId, validated, at, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "admin:notified user={User} devices={Devices} delivered={Delivered} by={Actor}",
            userId,
            outcome.DevicesTargeted,
            outcome.Delivered,
            actor.Email);

        return outcome;
    }

    /// <summary>
    /// How many customers a broadcast would reach, without sending anything.
    ///
    /// <para>
    /// The console calls this first and shows the number in the confirm dialog.
    /// "Send to 4,182 people" and "send to 12 people" are different decisions, and
    /// the only moment to learn which one this is, is before pressing send.
    /// </para>
    /// </summary>
    public async Task<int> BroadcastPreviewAsync(
        IReadOnlyCollection<ObjectId> recipients,
        CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        return recipients.Count;
    }

    /// <summary>Send to many. Refuses above <see cref="MaxBroadcastRecipients"/>.</summary>
    public async Task<BroadcastOutcome> BroadcastAsync(
        IReadOnlyCollection<ObjectId> recipients,
        string segment,
        AdminMessage message,
        AdminActor actor,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var validated = message.Validated();
        var justification = RequireReason(reason);

        if (recipients.Count == 0)
        {
            throw AppException.BadRequest("empty_segment", "That segment matches nobody right now.");
        }

        if (recipients.Count > MaxBroadcastRecipients)
        {
            throw AppException.BadRequest(
                "broadcast_too_large",
                $"That segment matches {recipients.Count} people, over the {MaxBroadcastRecipients} cap. "
                + "Narrow the segment — a broadcast cannot be recalled.");
        }

        var at = _time.GetUtcNow().UtcDateTime;

        await _audit.AppendAsync(
            new AdminAuditEventDocument
            {
                At = at,
                ActorId = actor.Id,
                ActorEmail = actor.Email,
                ActorRole = actor.Role,
                Action = AdminAuditAction.Broadcast,
                Reason = justification,
                Ip = actor.Ip,
                UserAgent = actor.UserAgent,
                Details = new BsonDocument
                {
                    ["segment"] = segment,
                    ["recipients"] = recipients.Count,
                    ["title"] = validated.Title,
                    ["body"] = validated.Body,
                },
            },
            cancellationToken).ConfigureAwait(false);

        var delivered = 0;
        var failed = 0;
        var inApp = 0;

        foreach (var userId in recipients)
        {
            // One recipient's failure must not abandon the rest — a broadcast that
            // stops halfway is the worst outcome, because nobody can tell where.
            try
            {
                var outcome = await DeliverAsync(userId, validated, at, cancellationToken)
                    .ConfigureAwait(false);

                delivered += outcome.Delivered;
                failed += outcome.Failed;
                if (outcome.InAppCreated)
                {
                    inApp++;
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "admin:broadcast-recipient-failed user={User}", userId);
            }
        }

        return new BroadcastOutcome(recipients.Count, delivered, failed, inApp);
    }

    /// <summary>
    /// Write the durable row, then ring every device.
    /// </summary>
    private async Task<NotifyOutcome> DeliverAsync(
        ObjectId userId,
        AdminMessage message,
        DateTime at,
        CancellationToken cancellationToken)
    {
        // FIRST, and unconditionally. See the type summary.
        await Notifications.InsertOneAsync(
            new NotificationDocument
            {
                SchemaVersion = 1,
                UserId = userId,
                Kind = AnnouncementKind,
                Title = message.Title,
                Body = message.Body,
                CreatedAt = at,
                UpdatedAt = at,
            },
            options: null,
            cancellationToken).ConfigureAwait(false);

        var devices = await _devices.GetActiveByUserIdAsync(userId.ToString()).ConfigureAwait(false);
        var results = new List<DeviceDelivery>(devices.Count);
        var delivered = 0;
        var failed = 0;

        foreach (var device in devices)
        {
            var result = await _push
                .SendAsync(
                    new PushNotificationRequest
                    {
                        DeviceToken = device.Token,
                        Title = message.Title,
                        Body = message.Body,

                        // The app routes on this. `announcement` has no target
                        // screen, so tapping it opens the notification list.
                        Data = new Dictionary<string, string> { ["kind"] = AnnouncementKind },
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var ok = result.IsSuccess;
            var code = result.Error?.Code;
            var detail = result.Error?.Message;

            if (ok)
            {
                delivered++;
            }
            else
            {
                failed++;

                // A token FCM has retired will never work again. Deactivating it
                // here is what stops the same dead device failing on every future
                // send and making the delivery numbers permanently look broken.
                if (code is not null && PushErrorCodes.IsTokenPermanentlyInvalid(code))
                {
                    await _devices.DeactivateAsync(device.Token, code).ConfigureAwait(false);
                }
            }

            results.Add(new DeviceDelivery(
                device.Platform.ToString(),
                PushTokenMask.Mask(device.Token),
                ok,

                // Code AND message: the code is what the console groups on, the
                // message is what makes one row diagnosable.
                ok ? null : $"{code}: {detail}"));
        }

        return new NotifyOutcome(devices.Count, delivered, failed, InAppCreated: true, results);
    }

    private static string RequireReason(string? reason)
    {
        var trimmed = reason?.Trim();

        if (string.IsNullOrEmpty(trimmed) || trimmed.Length < AdminCustomerService.MinReasonLength)
        {
            throw AppException.BadRequest(
                "reason_required",
                $"Sending a message needs a reason of at least {AdminCustomerService.MinReasonLength} characters.");
        }

        return trimmed;
    }
}
