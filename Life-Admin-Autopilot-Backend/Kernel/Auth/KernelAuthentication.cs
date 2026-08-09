using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot_Backend.Kernel.Errors;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;

namespace Life_Admin_Autopilot_Backend.Kernel.Auth;

public sealed class KernelAuthOptions : AuthenticationSchemeOptions
{
    public const string Scheme = "KernelBearer";
    public const string SectionName = "Kernel:Jwt";

    /// <summary>
    /// HS256 signing secret. Must equal Node's <c>JWT_ACCESS_SECRET</c> for the two
    /// servers to accept each other's tokens during parity testing. Falls back to
    /// <c>Jwt:Key</c> so an unconfigured environment still boots.
    /// </summary>
    public string AccessSecret { get; set; } = string.Empty;

    /// <summary>
    /// Node signs with <c>{ expiresIn, algorithm: 'HS256' }</c> and NOTHING else —
    /// no <c>iss</c>, no <c>aud</c>. Both validations therefore default to off.
    /// Turn one on only if the deployment starts issuing them.
    /// </summary>
    public string? ValidIssuer { get; set; }

    public string? ValidAudience { get; set; }
}

/// <summary>
/// Port of <c>server/src/middleware/auth.ts</c>.
///
/// <para><b>Two distinct 401s, and the codes are load-bearing:</b></para>
/// <list type="bullet">
///   <item>
///     header absent, or present but not starting with <c>"Bearer "</c> →
///     <c>401 missing_token</c> "Missing access token". Note a
///     <c>Authorization: Basic …</c> header takes THIS branch, not the invalid one —
///     verified live.
///   </item>
///   <item>
///     a Bearer token that fails verification (bad signature, expired, wrong
///     algorithm, missing <c>sub</c>/<c>email</c>) → <c>401 invalid_token</c>
///     "Invalid or expired access token".
///   </item>
/// </list>
///
/// <para>
/// <c>HS256</c> is pinned on VERIFY as well as sign. Without it an attacker can
/// swap the header to <c>alg: none</c> and bypass the signature check entirely.
/// </para>
/// </summary>
public sealed class KernelAuthenticationHandler : AuthenticationHandler<KernelAuthOptions>
{
    private const string BearerPrefix = "Bearer ";

    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public KernelAuthenticationHandler(
        IOptionsMonitor<KernelAuthOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    private AppException? PendingFailure
    {
        get => Context.Items.TryGetValue(typeof(KernelAuthenticationHandler), out var v) ? v as AppException : null;
        set => Context.Items[typeof(KernelAuthenticationHandler)] = value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header) || !header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            PendingFailure = AppException.Unauthorized("missing_token", "Missing access token");
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = header[BearerPrefix.Length..].Trim();

        try
        {
            var parameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Options.AccessSecret)),
                ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                ValidateLifetime = true,

                // jsonwebtoken has no clock tolerance by default; neither do we.
                ClockSkew = TimeSpan.Zero,
                ValidateIssuer = !string.IsNullOrEmpty(Options.ValidIssuer),
                ValidIssuer = Options.ValidIssuer,
                ValidateAudience = !string.IsNullOrEmpty(Options.ValidAudience),
                ValidAudience = Options.ValidAudience,
                NameClaimType = JwtRegisteredClaimNames.Sub,
            };

            var principal = _handler.ValidateToken(token, parameters, out _);

            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var email = principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value;

            // Node re-checks both claims after verify and rejects the token if either
            // is missing or not a string.
            if (string.IsNullOrEmpty(sub) || string.IsNullOrEmpty(email))
            {
                PendingFailure = AppException.Unauthorized("invalid_token", "Invalid or expired access token");
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(KernelAuthOptions.Scheme, JwtRegisteredClaimNames.Sub, ClaimTypes.Role);
            identity.AddClaim(new Claim(JwtRegisteredClaimNames.Sub, sub));
            identity.AddClaim(new Claim(JwtRegisteredClaimNames.Email, email));

            // Also under the framework names, so [Authorize] policies, User.Identity.Name
            // and anything reading ClaimTypes.NameIdentifier keep working.
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
            identity.AddClaim(new Claim(ClaimTypes.Email, email));

            foreach (var role in principal.FindAll(ClaimTypes.Role))
            {
                identity.AddClaim(role);
            }

            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), KernelAuthOptions.Scheme);
            PendingFailure = null;
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception)
        {
            PendingFailure = AppException.Unauthorized("invalid_token", "Invalid or expired access token");
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    /// <summary>
    /// Writes the error envelope instead of the framework's bare 401 +
    /// <c>WWW-Authenticate</c>. Node sends no challenge header at all.
    /// </summary>
    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var failure = PendingFailure
            ?? AppException.Unauthorized("missing_token", "Missing access token");

        await KernelErrorMiddleware.WriteAsync(
            Context,
            failure.Status,
            KernelErrorMiddleware.Envelope(failure.Code, failure.Message));
    }

    /// <summary>Node has no role model, so a forbid can only come from a slice policy.</summary>
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        KernelErrorMiddleware.WriteAsync(
            Context,
            403,
            KernelErrorMiddleware.Envelope("forbidden", "Forbidden"));
}

/// <summary>
/// <b>The claim shape every slice consumes.</b> Read the caller with
/// <c>HttpContext.RequireUser()</c> — never dig through <c>User.Claims</c> by hand,
/// and never assume <c>ClaimTypes.NameIdentifier</c> over <c>sub</c> or vice versa.
/// </summary>
/// <param name="Id">The Mongo ObjectId that every <c>userId</c> reference stores.</param>
/// <param name="IdString">The same value as the 24-hex string the API exposes.</param>
/// <param name="Email">The <c>email</c> claim. Always present — the handler rejects tokens without it.</param>
public readonly record struct KernelUser(ObjectId Id, string IdString, string Email);

public static class KernelUserAccessor
{
    /// <summary>
    /// The authenticated caller, or throws <c>401 missing_token</c>. Use inside any
    /// endpoint that carries <c>[Authorize]</c> — the throw is a belt-and-braces
    /// guard for a handler that forgot the attribute.
    /// </summary>
    public static KernelUser RequireUser(this HttpContext context) =>
        context.TryGetUser() ?? throw AppException.Unauthorized("missing_token", "Missing access token");

    public static KernelUser? TryGetUser(this HttpContext context)
    {
        var sub = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        if (string.IsNullOrEmpty(sub) || !ObjectId.TryParse(sub, out var id))
        {
            return null;
        }

        var email = context.User.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? context.User.FindFirst(ClaimTypes.Email)?.Value
            ?? string.Empty;

        return new KernelUser(id, sub, email);
    }
}
