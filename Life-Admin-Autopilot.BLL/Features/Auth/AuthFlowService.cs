using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Time;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Auth;

/// <summary>
/// Outbound mail. Split out because WHETHER A FAILURE IS VISIBLE differs per
/// call site and is contract surface: signup, forgot-password and magic-link are
/// fire-and-forget (a failure is logged and the response is unaffected), while
/// verify-email/send-code and change-email AWAIT the send and turn a failure into
/// <c>400 email_send_failed</c>.
/// </summary>
public interface IAuthEmailSender
{
    Task SendVerificationLinkAsync(string email, string rawToken, CancellationToken cancellationToken = default);

    Task SendPasswordResetAsync(string email, string rawToken, CancellationToken cancellationToken = default);

    Task SendMagicLinkAsync(string email, string rawToken, CancellationToken cancellationToken = default);

    Task SendCodeAsync(string email, string code, CancellationToken cancellationToken = default);
}

/// <summary>
/// The no-provider sender: logs and succeeds.
///
/// <para>
/// This is the parity target. The reference runs with no mail credentials in the
/// harness environment and its sends do not throw there — which is why
/// <c>email_send_failed</c> is marked <c>x-verified: source</c> in the contract,
/// never observed live. A sender that threw would turn documented 204s and 200s
/// into 400s.
/// </para>
/// </summary>
public sealed class LoggingAuthEmailSender : IAuthEmailSender
{
    private readonly ILogger<LoggingAuthEmailSender> _logger;

    public LoggingAuthEmailSender(ILogger<LoggingAuthEmailSender> logger) => _logger = logger;

    public Task SendVerificationLinkAsync(string email, string rawToken, CancellationToken cancellationToken = default) =>
        Log("auth:email:verification", email);

    public Task SendPasswordResetAsync(string email, string rawToken, CancellationToken cancellationToken = default) =>
        Log("auth:email:password-reset", email);

    public Task SendMagicLinkAsync(string email, string rawToken, CancellationToken cancellationToken = default) =>
        Log("auth:email:magic-link", email);

    public Task SendCodeAsync(string email, string code, CancellationToken cancellationToken = default) =>
        Log("auth:email:code", email);

    private Task Log(string evt, string email)
    {
        _logger.LogInformation("{Event} to={Email}", evt, email);
        return Task.CompletedTask;
    }
}

public interface IAuthFlowService
{
    /// <summary>Mints a link token, returning the RAW value to mail. Only the hash is stored.</summary>
    Task<string> IssueLinkTokenAsync(
        ObjectId userId,
        string purpose,
        TimeSpan ttl,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mints a 6-digit code, first DELETING any unconsumed code of the same purpose
    /// for the user — a resend invalidates its predecessor rather than widening the
    /// guessing window.
    /// </summary>
    Task<string> IssueCodeAsync(ObjectId userId, string purpose, CancellationToken cancellationToken = default);

    /// <summary>The owning user id, or null when the token is unknown/consumed/expired/wrong-purpose.</summary>
    Task<ObjectId?> ConsumeLinkTokenAsync(string rawToken, string purpose, CancellationToken cancellationToken = default);

    Task<bool> ConsumeCodeAsync(ObjectId userId, string purpose, string code, CancellationToken cancellationToken = default);
}

/// <summary>Port of <c>server/src/lib/authFlows.ts</c>.</summary>
public sealed class AuthFlowService : IAuthFlowService
{
    private readonly IVerificationTokenRepository _tokens;
    private readonly TimeProvider _clock;

    public AuthFlowService(IVerificationTokenRepository tokens, TimeProvider clock)
    {
        _tokens = tokens;
        _clock = clock;
    }

    public async Task<string> IssueLinkTokenAsync(
        ObjectId userId,
        string purpose,
        TimeSpan ttl,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var raw = AuthTokenHashing.NewSecret();

        // Link tokens do NOT delete their predecessors — several verification,
        // reset or magic links can legitimately be in flight at once.
        await _tokens.InsertAsync(
            new VerificationTokenDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                TokenHash = AuthTokenHashing.HashToken(raw),
                Purpose = purpose,
                ExpiresAt = now.Add(ttl),
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        return raw;
    }

    public async Task<string> IssueCodeAsync(
        ObjectId userId,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        await _tokens.DeleteUnconsumedAsync(userId, purpose, cancellationToken).ConfigureAwait(false);

        var code = AuthTokenHashing.NewSixDigitCode();

        await _tokens.InsertAsync(
            new VerificationTokenDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                TokenHash = AuthTokenHashing.HashCode(userId, purpose, code),
                Purpose = purpose,
                ExpiresAt = now.Add(AuthTtl.Code),
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        return code;
    }

    public async Task<ObjectId?> ConsumeLinkTokenAsync(
        string rawToken,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var consumed = await _tokens
            .ConsumeAsync(AuthTokenHashing.HashToken(rawToken), purpose, now, cancellationToken)
            .ConfigureAwait(false);

        return consumed?.UserId;
    }

    public Task<bool> ConsumeCodeAsync(
        ObjectId userId,
        string purpose,
        string code,
        CancellationToken cancellationToken = default) =>
        _tokens.ConsumeCodeAsync(
            userId,
            AuthTokenHashing.HashCode(userId, purpose, code),
            purpose,
            _clock.GetUtcNow().UtcDateTime,
            cancellationToken);
}

/// <summary>
/// Creates the two halves of a user — the Identity credential row and the Mongo
/// profile — and keeps the link between them.
/// </summary>
public interface IUserProvisioningService
{
    Task<UserProfileDocument> CreateAsync(string email, string? password, CancellationToken cancellationToken = default);
}

public sealed class UserProvisioningService : IUserProvisioningService
{
    private readonly IUserProfileRepository _users;
    private readonly IAuthCredentialStore _credentials;
    private readonly TimeProvider _clock;

    public UserProvisioningService(
        IUserProfileRepository users,
        IAuthCredentialStore credentials,
        TimeProvider clock)
    {
        _users = users;
        _credentials = credentials;
        _clock = clock;
    }

    /// <summary>
    /// The credential row is written FIRST so its Guid can key the profile. A
    /// failure after that point leaves an orphaned Identity row, which is
    /// unreachable (every lookup starts from the Mongo profile) and harmless.
    /// </summary>
    public async Task<UserProfileDocument> CreateAsync(
        string email,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var identityUserId = await _credentials.CreateAsync(email, password, cancellationToken).ConfigureAwait(false);

        var profile = new UserProfileDocument
        {
            Id = ObjectId.GenerateNewId(),
            IdentityUserId = identityUserId,
            Email = email,

            // Presence marker only — the real hash never leaves Identity. Absent for
            // a magic-link account, which is what makes hasPassword false.
            PasswordHash = password is null ? null : UserProfileRepository.PasswordPresentMarker,

            // Provisioned, not left null. An absent zone used to mean UTC to every
            // reader downstream, which is two or three hours off for an Egyptian
            // account and shows up as reminders firing early rather than as an
            // error.
            //
            // The flag is what keeps this a DEFAULT rather than a decision: the
            // client reads it and may replace the zone with the device's own on
            // first run, and picking one in Profile clears it. Without the flag,
            // provisioning a real value here would have silently killed device
            // detection, which used to trigger on the field being null.
            Timezone = AppTimeZone.DefaultId,
            TimezoneFollowsDevice = true,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _users.InsertAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }
}
