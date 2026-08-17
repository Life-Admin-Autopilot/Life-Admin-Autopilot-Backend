using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Life_Admin_Autopilot.BLL.Features.Admin;

/// <summary>
/// Admin token configuration.
///
/// <para>
/// <b>The secret is deliberately NOT the customer signing key.</b> A console token
/// and an app token are different capabilities and must not be interchangeable: with
/// a shared secret, the only thing standing between a customer's access token and
/// <c>/admin/*</c> would be a role claim the same key could sign. Separate keys make
/// that a cryptographic impossibility rather than a policy check.
/// </para>
///
/// <para>
/// <b>There is no refresh token.</b> A console session is short and ends by expiring;
/// a support laptop must not hold a live session overnight, and silent refresh is
/// exactly the mechanism that would let it.
/// </para>
/// </summary>
public sealed class AdminTokenOptions
{
    /// <summary>Signing secret. Console auth is disabled entirely when this is unset.</summary>
    public string? Secret { get; init; }

    /// <summary>
    /// Pinned so an admin token is rejected outright by the customer verifier and
    /// vice versa, independently of the key split.
    /// </summary>
    public string Issuer { get; init; } = "kitto-admin";

    public string Audience { get; init; } = "kitto-admin-console";

    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(8);

    /// <summary>HS256 needs 32 bytes; anything shorter is not a key, it is a password.</summary>
    public const int MinimumSecretBytes = 32;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Secret)
        && Encoding.UTF8.GetByteCount(Secret) >= MinimumSecretBytes;

    public static AdminTokenOptions FromConfiguration(IConfiguration configuration)
    {
        var secret = configuration["ADMIN_JWT_SECRET"] ?? configuration["Admin:Jwt:Secret"];
        var ttlHours = configuration["ADMIN_SESSION_HOURS"] ?? configuration["Admin:SessionHours"];

        return new AdminTokenOptions
        {
            Secret = string.IsNullOrWhiteSpace(secret) ? null : secret.Trim(),
            Ttl = int.TryParse(ttlHours, out var hours) && hours is > 0 and <= 24
                ? TimeSpan.FromHours(hours)
                : TimeSpan.FromHours(8),
        };
    }
}

/// <summary>A minted console session.</summary>
public sealed record AdminToken(string AccessToken, DateTime ExpiresAt, string Role);

/// <summary>Mints console tokens. Verification lives in the PL's authentication wiring.</summary>
public sealed class AdminTokenService
{
    /// <summary>The role claim, named so <c>[Authorize(Roles = …)]</c> reads it without configuration.</summary>
    public const string RoleClaim = ClaimTypes.Role;

    private readonly AdminTokenOptions _options;
    private readonly TimeProvider _time;

    public AdminTokenService(AdminTokenOptions options, TimeProvider? time = null)
    {
        _options = options;
        _time = time ?? TimeProvider.System;
    }

    public bool IsConfigured => _options.IsConfigured;

    /// <summary>
    /// Sign a console session for an identity that has already been authenticated
    /// <b>and</b> role-checked. This method does neither — it is the last step, not
    /// the gate.
    /// </summary>
    public AdminToken Issue(Guid identityId, string email, IReadOnlyCollection<string> roles)
    {
        if (!_options.IsConfigured)
        {
            throw new InvalidOperationException(
                "ADMIN_JWT_SECRET is unset or shorter than 32 bytes, so no console token can be signed. "
                + "The admin console is disabled until it is configured.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var expires = now.Add(_options.Ttl);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, identityId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),

            // A jti makes one session individually revocable later without changing
            // the token shape — the denylist is the only thing that would need adding.
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(RoleClaim, role));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret!));
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new AdminToken(
            new JwtSecurityTokenHandler().WriteToken(token),
            expires,
            AdminRoles.Highest(roles));
    }

    /// <summary>The validation parameters the PL registers the scheme with.</summary>
    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _options.Issuer,
        ValidateAudience = true,
        ValidAudience = _options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Secret ?? new string('0', 32))),
        ValidateLifetime = true,

        // The default five minutes of clock skew silently extends every session. A
        // console session is short on purpose; honour the expiry it was signed with.
        ClockSkew = TimeSpan.Zero,
        RoleClaimType = RoleClaim,
        NameClaimType = JwtRegisteredClaimNames.Email,
    };
}
