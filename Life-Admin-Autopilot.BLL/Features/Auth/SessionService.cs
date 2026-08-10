using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Life_Admin_Autopilot.BLL.Kernel.Auth;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.BLL.Features.Auth;

/// <summary>Where the caller's <c>User-Agent</c> and socket IP come from.</summary>
/// <param name="UserAgent">Already truncated to 256 characters, or null when the header was absent.</param>
/// <param name="Ip">The raw socket peer — <c>trust proxy</c> is off, so a forwarded header is ignored.</param>
public readonly record struct SessionMeta(string? UserAgent, string? Ip);

public sealed class AuthJwtOptions
{
    public string AccessSecret { get; set; } = string.Empty;

    public TimeSpan AccessTtl { get; set; } = AuthTtl.AccessToken;

    public TimeSpan RefreshTtl { get; set; } = AuthTtl.RefreshToken;
}

public interface ISessionService
{
    Task<AuthTokensDto> IssueAsync(
        UserProfileDocument user,
        string? replacePrevious,
        SessionMeta meta,
        CancellationToken cancellationToken = default);

    /// <summary>Null on every failure mode — the caller renders one indistinguishable 401.</summary>
    Task<AuthTokensDto?> RotateAsync(
        string rawToken,
        SessionMeta meta,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of <c>server/src/lib/sessions.ts</c>.
/// </summary>
public sealed class SessionService : ISessionService
{
    private readonly ISessionRepository _sessions;
    private readonly IUserProfileRepository _users;
    private readonly AuthJwtOptions _options;
    private readonly TimeProvider _clock;

    public SessionService(
        ISessionRepository sessions,
        IUserProfileRepository users,
        IOptions<AuthJwtOptions> options,
        TimeProvider clock)
    {
        _sessions = sessions;
        _users = users;
        _options = options.Value;
        _clock = clock;
    }

    /// <summary>
    /// Mints a token pair and records the session.
    ///
    /// <para>
    /// The successor row is INSERTED BEFORE the predecessor is revoked, and there
    /// is no transaction — a crash between the two leaves two live tokens. That
    /// ordering is Node's and is reproduced rather than tightened, because
    /// reversing it would open a window where the caller has no valid session at
    /// all.
    /// </para>
    /// </summary>
    public async Task<AuthTokensDto> IssueAsync(
        UserProfileDocument user,
        string? replacePrevious,
        SessionMeta meta,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var raw = AuthTokenHashing.NewSecret();
        var hash = AuthTokenHashing.HashToken(raw);

        await _sessions.InsertAsync(
            new RefreshTokenDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = user.Id,
                TokenHash = hash,
                ExpiresAt = now.Add(_options.RefreshTtl),
                UserAgent = meta.UserAgent,
                Ip = meta.Ip,
                LastUsedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            },
            cancellationToken).ConfigureAwait(false);

        if (replacePrevious is not null)
        {
            await _sessions.MarkRotatedAsync(
                await ResolveIdAsync(replacePrevious, cancellationToken).ConfigureAwait(false),
                hash,
                now,
                cancellationToken).ConfigureAwait(false);
        }

        return new AuthTokensDto
        {
            AccessToken = SignAccessToken(user.Id.ToString(), user.Email, now),
            RefreshToken = raw,
        };
    }

    /// <summary>
    /// Single-use rotation with reuse detection. The five failure modes below all
    /// return null and are rendered as one identical 401, so a caller cannot probe
    /// which one it hit.
    /// </summary>
    public async Task<AuthTokensDto?> RotateAsync(
        string rawToken,
        SessionMeta meta,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await _sessions
            .FindByHashAsync(AuthTokenHashing.HashToken(rawToken), cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return null;
        }

        // REUSE DETECTED. A revoked token being presented means the raw value
        // leaked or a client is replaying, so the entire device fleet is signed
        // out rather than just this one row. Note "family" is every unrevoked
        // token of the user, NOT a lineage walked through replacedBy — that field
        // is written for forensics and never read.
        if (existing.RevokedAt is not null)
        {
            await _sessions.RevokeAllAsync(existing.UserId, now, exceptHash: null, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        // Expiry does NOT trigger a family revoke — an honestly-aged token is not
        // evidence of compromise.
        if (existing.ExpiresAt <= now)
        {
            return null;
        }

        var user = await _users.FindByIdAsync(existing.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        // The new row inherits the old row's metadata only when the request supplies
        // none. Node uses `??`, so an empty-string User-Agent on the request WINS
        // over the stored value; only null falls back.
        return await IssueAsync(
            user,
            rawToken,
            new SessionMeta(meta.UserAgent ?? existing.UserAgent, meta.Ip ?? existing.Ip),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Node revokes the predecessor by hash with a bare <c>updateOne</c> — no owner
    /// scope and no not-yet-revoked guard. Resolving the id first keeps the
    /// repository API typed while preserving that.
    /// </summary>
    private async Task<ObjectId> ResolveIdAsync(string rawPrevious, CancellationToken cancellationToken)
    {
        var previous = await _sessions
            .FindByHashAsync(AuthTokenHashing.HashToken(rawPrevious), cancellationToken)
            .ConfigureAwait(false);

        return previous?.Id ?? ObjectId.Empty;
    }

    /// <summary>
    /// <c>{ sub, email }</c> signed HS256, with <c>iat</c>/<c>exp</c> and nothing
    /// else — no issuer, no audience, matching <c>lib/jwt.ts</c>. The kernel's
    /// verifier is configured the same way, so the two servers accept each other's
    /// tokens when they share a secret.
    /// </summary>
    private string SignAccessToken(string sub, string email, DateTime now)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.AccessSecret));
        var token = new JwtSecurityToken(
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, sub),
                new Claim(JwtRegisteredClaimNames.Email, email),
            },
            notBefore: null,
            expires: now.Add(_options.AccessTtl),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>Resolves the JWT secret through the same chain the kernel's verifier uses.</summary>
public static class AuthJwtConfiguration
{
    public static AuthJwtOptions Read(IConfiguration configuration) => new()
    {
        AccessSecret = JwtSecretResolver.Resolve(configuration),
        AccessTtl = ParseTtl(configuration["JWT_ACCESS_TTL"], AuthTtl.AccessToken),
        RefreshTtl = ParseTtl(configuration["JWT_REFRESH_TTL"], AuthTtl.RefreshToken),
    };

    /// <summary>Node's <c>ttlToMs</c>: <c>/^(\d+)([smhd])$/</c>, nothing else.</summary>
    public static TimeSpan ParseTtl(string? value, TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
        {
            return fallback;
        }

        var unit = value[^1];
        if (!int.TryParse(value[..^1], out var amount))
        {
            return fallback;
        }

        return unit switch
        {
            's' => TimeSpan.FromSeconds(amount),
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => fallback,
        };
    }
}
