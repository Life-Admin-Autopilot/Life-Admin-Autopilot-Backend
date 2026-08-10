using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>
/// Mirrors <c>IntegrationUnavailableError</c>.
///
/// <para>
/// <b>Deliberately NOT an <c>AppException</c>.</b> In Node this class does not
/// extend <c>AppError</c> either, so it falls through the error handler to a generic
/// <c>500 internal_error</c> — see the contract's note on <c>POST /sync</c>. Making
/// it a tidy 4xx here would be a parity break.
/// </para>
/// </summary>
public sealed class IntegrationUnavailableException : Exception
{
    public IntegrationUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Owns the lifecycle of a stored Google connection: minting usable access tokens,
/// persisting refreshes, and deciding when a connection is genuinely dead rather
/// than briefly unhappy. Port of
/// <c>server/src/modules/integrations/google/connection.ts</c>.
/// </summary>
public interface IGoogleConnectionService
{
    Task<IntegrationDocument?> FindAsync(ObjectId userId, CancellationToken cancellationToken = default);

    Task<IntegrationDocument> SaveConnectionAsync(
        ObjectId userId,
        GoogleTokens tokens,
        GoogleIdentity? identity,
        CancellationToken cancellationToken = default);

    /// <summary>A usable access token, refreshing if the cached one has lapsed.</summary>
    Task<string> GetAccessTokenAsync(IntegrationDocument integration, CancellationToken cancellationToken = default);

    /// <summary>Disconnect, and tell Google too.</summary>
    Task DisconnectAsync(IntegrationDocument integration, CancellationToken cancellationToken = default);

    /// <summary>Does this connection actually carry the scope a sync needs?</summary>
    bool HasScope(IntegrationDocument integration, string scope);
}

/// <summary>
/// The distinction between "dead" and "briefly unhappy" is the whole point of this
/// service. <c>invalid_grant</c> from Google means the refresh token will NEVER work
/// again. Retrying it is not just futile, it is harmful: the poller would spin
/// forever while the user sees a connection that claims to be fine and reminders
/// that quietly stopped. So that one error transitions the row to
/// <c>needs_reauth</c> and the UI is expected to say so.
/// </summary>
public sealed class GoogleConnectionService : IGoogleConnectionService
{
    private readonly IIntegrationRepository _integrations;
    private readonly IGoogleOAuthClient _oauth;
    private readonly IGoogleTokenCipher _cipher;
    private readonly TimeProvider _clock;
    private readonly ILogger<GoogleConnectionService> _logger;

    public GoogleConnectionService(
        IIntegrationRepository integrations,
        IGoogleOAuthClient oauth,
        IGoogleTokenCipher cipher,
        TimeProvider clock,
        ILogger<GoogleConnectionService> logger)
    {
        _integrations = integrations;
        _oauth = oauth;
        _cipher = cipher;
        _clock = clock;
        _logger = logger;
    }

    public Task<IntegrationDocument?> FindAsync(ObjectId userId, CancellationToken cancellationToken = default) =>
        _integrations.FindGoogleAsync(userId, cancellationToken);

    /// <summary>
    /// Create or update the connection after a successful consent.
    ///
    /// <para>
    /// The refresh token is PRESERVED when Google omits it. Google only returns one
    /// on a consent screen that actually prompted, so a re-consent that happens to
    /// skip it must not blank the only durable credential we hold.
    /// </para>
    /// </summary>
    public async Task<IntegrationDocument> SaveConnectionAsync(
        ObjectId userId,
        GoogleTokens tokens,
        GoogleIdentity? identity,
        CancellationToken cancellationToken = default)
    {
        if (!_cipher.IsConfigured)
        {
            throw new IntegrationUnavailableException("Token storage is not configured on this server.");
        }

        var existing = await _integrations.FindGoogleAsync(userId, cancellationToken).ConfigureAwait(false);

        var refreshTokenEnc = tokens.RefreshToken is { Length: > 0 }
            ? _cipher.Encrypt(tokens.RefreshToken)
            : existing?.RefreshTokenEnc;

        if (string.IsNullOrEmpty(refreshTokenEnc))
        {
            // No new token and nothing stored — the connection would be dead on
            // arrival. Almost always means access_type=offline or prompt=consent was
            // dropped from the authorize URL.
            throw new IntegrationUnavailableException(
                "Google did not return a refresh token. Try connecting again.");
        }

        var now = _clock.GetUtcNow().UtcDateTime;

        var replacement = new IntegrationDocument
        {
            // A replace keeps the existing _id when one is there; on an upsert the
            // driver needs a fresh one, exactly as Mongoose mints on insert.
            Id = existing?.Id ?? ObjectId.GenerateNewId(now),
            UserId = userId,
            Provider = IntegrationVocabulary.Google,
            ExternalAccountId = identity?.Sub ?? existing?.ExternalAccountId ?? "unknown",
            ExternalAccountEmail = identity?.Email ?? existing?.ExternalAccountEmail,
            RefreshTokenEnc = refreshTokenEnc,
            AccessTokenEnc = _cipher.Encrypt(tokens.AccessToken),
            AccessTokenExpiresAt = tokens.ExpiresAt,
            GrantedScopes = tokens.GrantedScopes.ToList(),
            Status = IntegrationVocabulary.StatusActive,

            // `lastError: undefined` and `revokedAt: undefined` in the $set — Mongoose
            // treats an undefined value as "leave unset", and the row is being
            // rewritten wholesale, so both simply do not appear.
            LastError = null,
            RevokedAt = null,

            // Sync cursors are NOT part of the $set, so a reconnect keeps them.
            CalendarSyncToken = existing?.CalendarSyncToken,
            CalendarSyncedAt = existing?.CalendarSyncedAt,
            TasksSyncedAt = existing?.TasksSyncedAt,
            ImportDomain = existing?.ImportDomain ?? IntegrationVocabulary.DefaultImportDomain,
            ConnectedAt = existing?.ConnectedAt ?? now,
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now,
        };

        return await _integrations.UpsertGoogleAsync(replacement, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> GetAccessTokenAsync(
        IntegrationDocument integration,
        CancellationToken cancellationToken = default)
    {
        if (integration.Status != IntegrationVocabulary.StatusActive)
        {
            throw new IntegrationUnavailableException("That Google account needs reconnecting.");
        }

        var cached = integration.AccessTokenEnc;
        var expiry = integration.AccessTokenExpiresAt;
        if (!string.IsNullOrEmpty(cached) && expiry.HasValue && expiry.Value > _clock.GetUtcNow().UtcDateTime)
        {
            try
            {
                return _cipher.Decrypt(cached);
            }
            catch (Exception ex) when (ex is DecryptionFailedException or EncryptionNotConfiguredException)
            {
                // A cached token we cannot read is not fatal — it is a cache. Fall
                // through to a refresh rather than failing the request.
                _logger.LogWarning(
                    ex,
                    "google:access-token-unreadable integrationId={IntegrationId}",
                    integration.Id);
            }
        }

        string refreshToken;
        try
        {
            refreshToken = _cipher.Decrypt(integration.RefreshTokenEnc);
        }
        catch (Exception ex) when (ex is DecryptionFailedException or EncryptionNotConfiguredException)
        {
            // The refresh token is the durable credential. If THAT will not decrypt,
            // the encryption key changed or the row is corrupt, and no amount of
            // retrying fixes it — the user has to reconnect.
            _logger.LogError(ex, "google:refresh-token-unreadable integrationId={IntegrationId}", integration.Id);
            await MarkNeedsReauthAsync(integration, "Stored credentials could not be read.", cancellationToken)
                .ConfigureAwait(false);
            throw new IntegrationUnavailableException("That Google account needs reconnecting.");
        }

        try
        {
            var tokens = await _oauth.RefreshAccessTokenAsync(refreshToken, cancellationToken).ConfigureAwait(false);

            integration.AccessTokenEnc = _cipher.Encrypt(tokens.AccessToken);
            integration.AccessTokenExpiresAt = tokens.ExpiresAt;

            // A refresh response carries the CURRENT grant, so scopes the user has
            // since removed disappear here. Keeping the stale list would let a sync
            // call an API it no longer has permission for and 403 on every tick.
            if (tokens.GrantedScopes.Count > 0)
            {
                integration.GrantedScopes = tokens.GrantedScopes.ToList();
            }

            integration.LastError = null;
            integration.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            await _integrations.SaveAsync(integration, cancellationToken).ConfigureAwait(false);

            return tokens.AccessToken;
        }
        catch (GoogleOAuthException ex) when (ex.NeedsReauth)
        {
            await MarkNeedsReauthAsync(integration, "Kitto lost access to your Google account.", cancellationToken)
                .ConfigureAwait(false);
            throw new IntegrationUnavailableException("That Google account needs reconnecting.");
        }

        // Anything else — a timeout, a 5xx — is transient and propagates unchanged.
        // Leave the row active so the next poll tries again.
    }

    /// <summary>
    /// The revoke call is best-effort: if it fails the local row is still removed,
    /// because a user who pressed Disconnect must not be left connected by a network
    /// blip on our side. The reverse (dropping the row but staying authorised at
    /// Google) is the failure that matters, and this ordering avoids it.
    /// </summary>
    public async Task DisconnectAsync(
        IntegrationDocument integration,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var refreshToken = _cipher.Decrypt(integration.RefreshTokenEnc);
            await _oauth.RevokeTokenAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "google:revoke-failed integrationId={IntegrationId}", integration.Id);
        }

        await _integrations.DeleteAsync(integration.Id, cancellationToken).ConfigureAwait(false);
    }

    public bool HasScope(IntegrationDocument integration, string scope) =>
        integration.GrantedScopes.Contains(scope);

    private Task MarkNeedsReauthAsync(
        IntegrationDocument integration,
        string reason,
        CancellationToken cancellationToken)
    {
        integration.Status = IntegrationVocabulary.StatusNeedsReauth;
        integration.LastError = reason;
        integration.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
        return _integrations.SaveAsync(integration, cancellationToken);
    }
}
