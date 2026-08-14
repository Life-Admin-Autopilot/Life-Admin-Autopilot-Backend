using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>Mirrors <c>GoogleNotConfiguredError</c>.</summary>
public sealed class GoogleNotConfiguredException : Exception
{
    public GoogleNotConfiguredException()
        : base("Google is not configured on this server.")
    {
    }
}

/// <summary>Mirrors <c>GoogleOAuthError</c>.</summary>
public sealed class GoogleOAuthException : Exception
{
    public GoogleOAuthException(string message, bool needsReauth = false)
        : base(message)
    {
        NeedsReauth = needsReauth;
    }

    /// <summary>
    /// <c>invalid_grant</c> only. It means the refresh token will NEVER work again —
    /// the user revoked access, changed their password, or left it unused for six
    /// months. Retrying is not just futile, it is harmful.
    /// </summary>
    public bool NeedsReauth { get; }
}

/// <param name="RefreshToken">Absent on refresh responses — only the initial exchange returns one.</param>
/// <param name="GrantedScopes">What Google ACTUALLY granted, which may be less than we asked for.</param>
public sealed record GoogleTokens(
    string AccessToken,
    string? RefreshToken,
    DateTime ExpiresAt,
    IReadOnlyList<string> GrantedScopes);

/// <summary>Claims taken from the <c>id_token</c>.</summary>
public sealed record GoogleIdentity(string Sub, string? Email);

/// <summary>
/// Google OAuth 2.0, server side. Port of
/// <c>server/src/modules/integrations/google/oauthClient.ts</c>.
/// </summary>
public interface IGoogleOAuthClient
{
    bool IsConfigured { get; }

    string BuildAuthorizeUrl(string state);

    Task<(GoogleTokens Tokens, GoogleIdentity? Identity)> ExchangeCodeAsync(
        string code,
        CancellationToken cancellationToken = default);

    Task<GoogleTokens> RefreshAccessTokenAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Tell Google to forget us. Best-effort by design.</summary>
    Task RevokeTokenAsync(string token, CancellationToken cancellationToken = default);
}

/// <summary>
/// The flow runs entirely on the server rather than in the app, for three reasons:
/// Google returns <c>disallowed_useragent</c> for OAuth attempted inside an embedded
/// webview (which is exactly what the Capacitor shell is); the server needs the
/// refresh token regardless, because background polling runs while the phone is
/// asleep; and keeping the refresh token off the device means a stolen phone does
/// not hand over someone's calendar.
/// </summary>
public sealed class GoogleOAuthClient : IGoogleOAuthClient
{
    public const string HttpClientName = "google-oauth";

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    /// <summary>
    /// Narrow on purpose. <c>calendar.readonly</c> and <c>tasks.readonly</c> are
    /// SENSITIVE scopes — they need Google verification but NOT a CASA security
    /// assessment.
    ///
    /// <para>
    /// <see cref="ScopeCalendarApp"/> is what lets Kitto write matters BACK, and it
    /// is deliberately not the writable <c>calendar</c> scope. That one grants "see,
    /// edit, share, and permanently delete all the calendars you can access" — the
    /// scariest prompt Google shows — and it would put a user's real appointments
    /// one bug away from deletion. <c>calendar.app.created</c> reaches only calendars
    /// this app itself created, so the blast radius of everything in
    /// <see cref="GoogleCalendarPushService"/> is a calendar Kitto made and the user
    /// can delete in one click.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> Scopes = new[]
    {
        "openid",
        "email",
        ScopeCalendar,
        ScopeTasks,
        ScopeCalendarApp,
    };

    public const string ScopeCalendar = "https://www.googleapis.com/auth/calendar.readonly";
    public const string ScopeTasks = "https://www.googleapis.com/auth/tasks.readonly";

    /// <summary>Read/write, but ONLY on calendars this application created.</summary>
    public const string ScopeCalendarApp = "https://www.googleapis.com/auth/calendar.app.created";

    private static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RevokeTimeout = TimeSpan.FromSeconds(10);

    private readonly GoogleIntegrationOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _clock;

    public GoogleOAuthClient(
        GoogleIntegrationOptions options,
        IHttpClientFactory httpClientFactory,
        TimeProvider clock)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _clock = clock;
    }

    public bool IsConfigured => _options.IsGoogleConfigured;

    public string BuildAuthorizeUrl(string state)
    {
        RequireConfigured();

        // Parameter order matches the Node `searchParams.set` sequence, and the
        // encoding is URLSearchParams', not RFC 3986 — space becomes '+', and '~'
        // is escaped. See FormUrlEncode.
        var query = new (string Key, string Value)[]
        {
            ("client_id", _options.ClientId!),
            ("redirect_uri", _options.RedirectUri!),
            ("response_type", "code"),
            ("scope", string.Join(' ', Scopes)),
            ("state", state),

            // Without access_type=offline Google issues no refresh token at all, and
            // the connection dies silently an hour later.
            ("access_type", "offline"),

            // And without prompt=consent it only issues one on the user's FIRST ever
            // consent — so anyone who connects, disconnects and reconnects would get
            // an access token with no way to renew it. The single most commonly
            // missed parameter in Google OAuth.
            ("prompt", "consent"),
            ("include_granted_scopes", "true"),
        };

        return $"{AuthEndpoint}?{FormUrlEncode(query)}";
    }

    public async Task<(GoogleTokens Tokens, GoogleIdentity? Identity)> ExchangeCodeAsync(
        string code,
        CancellationToken cancellationToken = default)
    {
        RequireConfigured();

        var json = await PostTokenAsync(
                new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = _options.ClientId!,
                    ["client_secret"] = _options.ClientSecret!,
                    ["redirect_uri"] = _options.RedirectUri!,
                    ["grant_type"] = "authorization_code",
                },
                cancellationToken)
            .ConfigureAwait(false);

        return (ToTokens(json), DecodeIdentity(json.IdToken));
    }

    public async Task<GoogleTokens> RefreshAccessTokenAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        RequireConfigured();

        var json = await PostTokenAsync(
                new Dictionary<string, string>
                {
                    ["refresh_token"] = refreshToken,
                    ["client_id"] = _options.ClientId!,
                    ["client_secret"] = _options.ClientSecret!,
                    ["grant_type"] = "refresh_token",
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ToTokens(json);
    }

    /// <summary>
    /// If this fails the local row is still deleted, because a user who pressed
    /// Disconnect must not be left connected by a network blip on our side.
    /// </summary>
    public async Task RevokeTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RevokeTimeout);

        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        using var response = await Client()
            .PostAsync(RevokeEndpoint, content, timeout.Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The signature is NOT verified, and that is correct here rather than lazy: this
    /// token came straight from Google's token endpoint over TLS in a request we
    /// initiated, which Google's own documentation names as the case where
    /// verification can be skipped. An id_token arriving from anywhere else would
    /// have to be verified.
    /// </summary>
    public static GoogleIdentity? DecodeIdentity(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken))
        {
            return null;
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2 || parts[1].Length == 0 || !UrlSafeBase64.TryDecode(parts[1], out var payload))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("sub", out var sub)
                || sub.ValueKind != JsonValueKind.String
                || sub.GetString() is not { Length: > 0 } subject)
            {
                return null;
            }

            var email = document.RootElement.TryGetProperty("email", out var e)
                && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;

            return new GoogleIdentity(subject, email);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The <c>application/x-www-form-urlencoded</c> serializer WHATWG's
    /// URLSearchParams uses: everything outside <c>[A-Za-z0-9*\-._]</c> is
    /// percent-encoded from its UTF-8 bytes, and a space becomes <c>+</c>.
    /// <c>Uri.EscapeDataString</c> is not a substitute — it emits <c>%20</c> and
    /// leaves <c>~</c> and <c>!</c> alone.
    /// </summary>
    public static string FormUrlEncode(IEnumerable<(string Key, string Value)> pairs) =>
        string.Join('&', pairs.Select(p => $"{EncodeComponent(p.Key)}={EncodeComponent(p.Value)}"));

    private static string EncodeComponent(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '*' or '-' or '.' or '_')
            {
                builder.Append(c);
            }
            else if (c == ' ')
            {
                builder.Append('+');
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2"));
            }
        }

        return builder.ToString();
    }

    private HttpClient Client() => _httpClientFactory.CreateClient(HttpClientName);

    private void RequireConfigured()
    {
        if (!_options.IsGoogleConfigured)
        {
            throw new GoogleNotConfiguredException();
        }
    }

    private async Task<TokenResponse> PostTokenAsync(
        Dictionary<string, string> body,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TokenTimeout);

        using var content = new FormUrlEncodedContent(body);
        using var response = await Client()
            .PostAsync(TokenEndpoint, content, timeout.Token)
            .ConfigureAwait(false);

        TokenResponse json;
        try
        {
            json = await response.Content
                .ReadFromJsonAsync<TokenResponse>(cancellationToken: timeout.Token)
                .ConfigureAwait(false) ?? new TokenResponse();
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or NotSupportedException)
        {
            json = new TokenResponse();
        }

        if (!response.IsSuccessStatusCode)
        {
            var needsReauth = json.Error == "invalid_grant";
            throw new GoogleOAuthException(
                json.ErrorDescription ?? json.Error ?? $"Google returned {(int)response.StatusCode}",
                needsReauth);
        }

        return json;
    }

    private GoogleTokens ToTokens(TokenResponse json)
    {
        if (string.IsNullOrEmpty(json.AccessToken))
        {
            throw new GoogleOAuthException("Google returned no access token.");
        }

        // Expire a minute early so a token cannot lapse mid-request.
        var ttl = (json.ExpiresIn ?? 3600) - 60;

        return new GoogleTokens(
            json.AccessToken,
            json.RefreshToken,
            _clock.GetUtcNow().UtcDateTime.AddSeconds(ttl),
            string.IsNullOrEmpty(json.Scope)
                ? Array.Empty<string>()
                : json.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class TokenResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public int? ExpiresIn { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("id_token")]
        public string? IdToken { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string? Error { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("error_description")]
        public string? ErrorDescription { get; set; }
    }
}
