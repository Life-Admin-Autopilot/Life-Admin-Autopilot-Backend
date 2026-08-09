using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;
using Life_Admin_Autopilot.DAL.Features.Auth;

namespace Life_Admin_Autopilot.BLL.Features.Auth;

/// <summary>
/// The token pair. <c>accessToken</c> is a Node-shaped HS256 JWT carrying only
/// <c>{sub, email, iat, exp}</c>; <c>refreshToken</c> is opaque — 32 CSPRNG bytes
/// as 43 base64url characters, of which only the sha256 is ever stored.
/// </summary>
public sealed class AuthTokensDto
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>signup, signin and magic-consume. <c>/auth/refresh</c> does NOT use this — it returns tokens alone.</summary>
public sealed class AuthSessionResponse
{
    [JsonPropertyName("user")]
    public UserDto User { get; init; } = new();

    [JsonPropertyName("tokens")]
    public AuthTokensDto Tokens { get; init; } = new();
}

public sealed class AuthUserResponse
{
    [JsonPropertyName("user")]
    public UserDto User { get; init; } = new();
}

/// <summary>The <c>/auth/refresh</c> body. No <c>user</c> key — deliberately.</summary>
public sealed class AuthTokensResponse
{
    [JsonPropertyName("tokens")]
    public AuthTokensDto Tokens { get; init; } = new();
}

/// <summary>
/// One row of <c>/auth/sessions/list</c>. A deliberately narrow hand-built
/// projection of the refresh-token document: <c>tokenHash</c>, <c>expiresAt</c>,
/// <c>revokedAt</c>, <c>replacedBy</c> and <c>userId</c> are all withheld.
/// The four optional members are OMITTED when unset, never null.
/// </summary>
public sealed class AuthSessionDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("current")]
    public bool Current { get; init; }

    [JsonPropertyName("userAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserAgent { get; init; }

    [JsonPropertyName("ip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ip { get; init; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedAt { get; init; }

    [JsonPropertyName("lastUsedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastUsedAt { get; init; }
}

public sealed class AuthSessionsResponse
{
    [JsonPropertyName("sessions")]
    public IReadOnlyList<AuthSessionDto> Sessions { get; init; } = Array.Empty<AuthSessionDto>();
}

public static class AuthSessionMapper
{
    /// <summary>
    /// <paramref name="currentHash"/> is the sha256 of the refresh token the caller
    /// supplied, or null when they supplied none — in which case EVERY row reports
    /// <c>current: false</c>.
    /// </summary>
    public static AuthSessionDto ToDto(this RefreshTokenDocument doc, string? currentHash) => new()
    {
        Id = doc.Id.ToString(),
        Current = currentHash is not null && doc.TokenHash == currentHash,
        UserAgent = doc.UserAgent,
        Ip = doc.Ip,
        CreatedAt = doc.CreatedAt,
        LastUsedAt = doc.LastUsedAt,
    };
}
