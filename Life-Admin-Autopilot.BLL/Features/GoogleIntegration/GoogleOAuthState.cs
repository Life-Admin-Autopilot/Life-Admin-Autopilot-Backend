using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>Mirrors <c>InvalidOAuthStateError</c>. Never described to the caller.</summary>
public sealed class InvalidOAuthStateException : Exception
{
    public InvalidOAuthStateException(string reason)
        : base($"OAuth state rejected: {reason}")
    {
    }
}

/// <param name="UserId">The Kitto user id this consent belongs to.</param>
/// <param name="Web">True when the flow began in a plain browser, so there is no app to deep-link back into.</param>
public readonly record struct OAuthStateClaims(string UserId, bool Web);

/// <summary>
/// The <c>state</c> parameter, signed. Port of
/// <c>server/src/modules/integrations/google/oauthState.ts</c>.
/// </summary>
public interface IGoogleOAuthState
{
    string Issue(string userId, bool web = false);

    /// <summary>Returns the claims the state was minted with, or throws.</summary>
    OAuthStateClaims Verify(string? state);
}

/// <summary>
/// <b>This is the CSRF boundary of the whole OAuth flow, and getting it wrong is not
/// a subtle bug.</b> The callback arrives as an unauthenticated GET from Google's
/// redirect — no session cookie, no Authorization header — so the ONLY thing telling
/// us which Kitto account to attach the tokens to is <c>state</c>. If an attacker
/// can forge or replay it they can bind their own Google account to someone else's
/// Kitto account, and from then on the victim's imported matters are the attacker's
/// data. The reverse — binding a victim's Google account into an attacker's Kitto
/// account — leaks the victim's calendar outright.
///
/// <para>
/// So: HMAC-SHA256 over the user id plus an expiry plus a nonce.
/// </para>
///
/// <para>
/// <b>The signing key is DERIVED from the access-token secret rather than being
/// it</b> — <c>HMAC-SHA256(JWT_ACCESS_SECRET, "kitto:oauth-state:v1")</c>. That
/// domain separation means a state token can never be presented as a session token,
/// or vice versa, even if some future code path confuses the two.
/// </para>
/// </summary>
public sealed class GoogleOAuthState : IGoogleOAuthState
{
    /// <summary>
    /// A user has to get through Google's consent screen in this window. Generous
    /// enough to read the permissions and pick an account, short enough that a leaked
    /// URL from a browser history is useless.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private const string DomainSeparator = "kitto:oauth-state:v1";

    private readonly byte[] _signingKey;
    private readonly TimeProvider _clock;

    public GoogleOAuthState(GoogleIntegrationOptions options, TimeProvider clock)
    {
        _signingKey = DeriveSigningKey(options.AccessSecret);
        _clock = clock;
    }

    /// <summary><c>createHmac('sha256', JWT_ACCESS_SECRET).update('kitto:oauth-state:v1').digest()</c>.</summary>
    public static byte[] DeriveSigningKey(string accessSecret) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(accessSecret), Encoding.UTF8.GetBytes(DomainSeparator));

    public string Issue(string userId, bool web = false)
    {
        var expiry = _clock.GetUtcNow().ToUnixTimeMilliseconds() + (long)Ttl.TotalMilliseconds;
        var nonce = UrlSafeBase64.Encode(RandomNumberGenerator.GetBytes(9));

        // Written by hand rather than serialized from a type so the key ORDER matches
        // Node's object literal exactly (uid, exp, n, then the optional w). A state
        // minted here is then byte-identical to one Node would mint for the same
        // inputs, which is what makes a cross-implementation check meaningful.
        var payload = new StringBuilder()
            .Append("{\"uid\":").Append(JsonSerializer.Serialize(userId))
            .Append(",\"exp\":").Append(expiry)
            .Append(",\"n\":").Append(JsonSerializer.Serialize(nonce));

        if (web)
        {
            payload.Append(",\"w\":true");
        }

        payload.Append('}');

        var body = UrlSafeBase64.EncodeUtf8(payload.ToString());
        return $"{body}.{Sign(body)}";
    }

    public OAuthStateClaims Verify(string? state)
    {
        if (string.IsNullOrEmpty(state))
        {
            throw new InvalidOAuthStateException("missing");
        }

        var parts = state.Split('.');
        if (parts.Length != 2)
        {
            throw new InvalidOAuthStateException("malformed");
        }

        var (body, signature) = (parts[0], parts[1]);
        if (body.Length == 0 || signature.Length == 0)
        {
            throw new InvalidOAuthStateException("malformed");
        }

        // Length must match before the fixed-time compare, which is undefined for
        // mismatched lengths. Compared in constant time so the signature cannot be
        // recovered a byte at a time by timing the response.
        var provided = Encoding.UTF8.GetBytes(signature);
        var computed = Encoding.UTF8.GetBytes(Sign(body));
        if (provided.Length != computed.Length || !CryptographicOperations.FixedTimeEquals(provided, computed))
        {
            throw new InvalidOAuthStateException("bad signature");
        }

        // A valid signature proves WE produced the body, not that the body is well
        // formed. Signed garbage still must not reach the parser unguarded.
        if (!UrlSafeBase64.TryDecode(body, out var decoded))
        {
            throw new InvalidOAuthStateException("unreadable payload");
        }

        JsonElement payload;
        try
        {
            using var document = JsonDocument.Parse(decoded);
            payload = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InvalidOAuthStateException("unreadable payload");
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOAuthStateException("unreadable payload");
        }

        if (!payload.TryGetProperty("uid", out var uid)
            || uid.ValueKind != JsonValueKind.String
            || uid.GetString() is not { Length: > 0 } userId)
        {
            throw new InvalidOAuthStateException("no subject");
        }

        if (!payload.TryGetProperty("exp", out var exp)
            || exp.ValueKind != JsonValueKind.Number
            || !exp.TryGetInt64(out var expiry)
            || _clock.GetUtcNow().ToUnixTimeMilliseconds() > expiry)
        {
            throw new InvalidOAuthStateException("expired");
        }

        var web = payload.TryGetProperty("w", out var w) && w.ValueKind == JsonValueKind.True;
        return new OAuthStateClaims(userId, web);
    }

    private string Sign(string body) =>
        UrlSafeBase64.Encode(HMACSHA256.HashData(_signingKey, Encoding.UTF8.GetBytes(body)));
}
