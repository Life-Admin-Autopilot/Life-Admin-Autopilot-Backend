using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

namespace Life_Admin_Autopilot.Tests.Features.GoogleIntegration;

/// <summary>
/// The OAuth state token. <b>The parity harness cannot reach any of this</b> — every
/// harness row runs against a server with no Google credentials, so no state is ever
/// minted or verified over HTTP — and it is the CSRF boundary of the whole OAuth
/// flow, so it is unit tested directly.
///
/// <para>
/// Mirrors <c>modules/integrations/google/oauthState.test.ts</c> case for case, plus
/// three the Node suite does not cover: the derived signing key, cross-secret
/// rejection, and the empty-signature split.
/// </para>
/// </summary>
public sealed class GoogleOAuthStateTests
{
    private const string User = "507f1f77bcf86cd799439011";
    private const string Secret = "kernel-test-access-secret-at-least-32-chars-long";

    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private static GoogleOAuthState Create(DateTimeOffset? now = null, string secret = Secret) =>
        new(new GoogleIntegrationOptions { AccessSecret = secret }, new FixedClock(now ?? Now));

    [Fact]
    public void round_trips_the_user_it_was_minted_for()
    {
        // Arrange
        var state = Create();

        // Act
        var claims = state.Verify(state.Issue(User));

        // Assert
        Assert.Equal(User, claims.UserId);
    }

    [Fact]
    public void carries_the_web_flag_through_the_signature()
    {
        // Arrange — read from the SIGNED payload, never the callback query. The
        // callback is unauthenticated, so a query-supplied flag would let an attacker
        // choose where our OAuth exit sends the user.
        var state = Create();

        // Act & Assert
        Assert.True(state.Verify(state.Issue(User, web: true)).Web);
        Assert.False(state.Verify(state.Issue(User)).Web);
    }

    [Fact]
    public void issues_a_different_state_every_time()
    {
        // Arrange — the nonce is what stops an identical payload being replayed
        // inside the validity window. Note the clock is FIXED here, so only the nonce
        // can make the two differ.
        var state = Create();

        // Act & Assert
        Assert.NotEqual(state.Issue(User), state.Issue(User));
    }

    [Fact]
    public void rejects_a_forged_subject()
    {
        // Arrange — THE attack this module exists to stop. The callback is an
        // unauthenticated GET, so a state an attacker can rewrite means they choose
        // whose account the Google tokens land on.
        var state = Create();
        var issued = state.Issue(User);
        var parts = issued.Split('.');

        Assert.True(UrlSafeBase64.TryDecode(parts[0], out var bodyBytes));
        var claims = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bodyBytes)!;
        claims["uid"] = JsonSerializer.SerializeToElement("507f1f77bcf86cd799439099");

        var forged = UrlSafeBase64.EncodeUtf8(JsonSerializer.Serialize(claims));

        // Act & Assert
        Assert.Throws<InvalidOAuthStateException>(() => state.Verify($"{forged}.{parts[1]}"));
    }

    [Fact]
    public void rejects_a_tampered_signature()
    {
        // Arrange
        var state = Create();
        var parts = state.Issue(User).Split('.');
        var tampered = $"{parts[0]}.{parts[1][..^1]}x";

        // Act & Assert
        Assert.Throws<InvalidOAuthStateException>(() => state.Verify(tampered));
    }

    [Fact]
    public void rejects_a_signature_of_the_wrong_length_without_throwing_on_the_comparison()
    {
        // Arrange — a fixed-time compare is undefined for mismatched lengths, so the
        // length guard has to come first. Otherwise a short signature is a 500 instead
        // of a clean rejection, and on this route a 500 is a JSON envelope where the
        // contract demands a redirect.
        var state = Create();
        var body = state.Issue(User).Split('.')[0];

        // Act & Assert
        Assert.Throws<InvalidOAuthStateException>(() => state.Verify($"{body}.short"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nodot")]
    [InlineData("a.b.c")]
    [InlineData(".onlysignature")]
    [InlineData("onlybody.")]
    public void rejects_malformed_and_missing_states(string? bad)
    {
        // Arrange
        var state = Create();

        // Act & Assert
        Assert.Throws<InvalidOAuthStateException>(() => state.Verify(bad));
    }

    [Fact]
    public void rejects_an_expired_state()
    {
        // Arrange — 11 minutes on, past the 10-minute window.
        var issuer = Create();
        var issued = issuer.Issue(User);
        var later = Create(Now.AddMinutes(11));

        // Act
        var error = Assert.Throws<InvalidOAuthStateException>(() => later.Verify(issued));

        // Assert
        Assert.Contains("expired", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void accepts_a_state_one_second_before_it_expires()
    {
        // Arrange — the boundary is `now > exp`, so exactly-at-expiry is still valid.
        var issuer = Create();
        var issued = issuer.Issue(User);
        var later = Create(Now.Add(GoogleOAuthState.Ttl));

        // Act & Assert
        Assert.Equal(User, later.Verify(issued).UserId);
    }

    [Fact]
    public void rejects_a_payload_whose_body_is_not_valid_json()
    {
        // Arrange — signed garbage still must not reach the parser unguarded. A
        // signing key is not a guarantee the body is well formed, only that we
        // produced it. Deliberately pair a real signature with a different body: this
        // must fail at the SIGNATURE check, proving the two are bound together.
        var state = Create();
        var real = state.Issue(User);
        var garbage = UrlSafeBase64.EncodeUtf8("not json");

        // Act
        var error = Assert.Throws<InvalidOAuthStateException>(() => state.Verify($"{garbage}.{real.Split('.')[1]}"));

        // Assert
        Assert.Contains("bad signature", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void rejects_a_correctly_signed_body_that_is_not_json()
    {
        // Arrange — the same garbage, but signed with the real key, so it gets past
        // the signature check and has to be stopped by the parse guard.
        var state = Create();
        var signed = SignWith(Secret, UrlSafeBase64.EncodeUtf8("not json"));

        // Act
        var error = Assert.Throws<InvalidOAuthStateException>(() => state.Verify(signed));

        // Assert
        Assert.Contains("unreadable payload", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void rejects_a_correctly_signed_body_with_no_subject()
    {
        // Arrange
        var state = Create();
        var expiry = Now.AddMinutes(5).ToUnixTimeMilliseconds();
        var signed = SignWith(Secret, UrlSafeBase64.EncodeUtf8($"{{\"exp\":{expiry},\"n\":\"abc\"}}"));

        // Act
        var error = Assert.Throws<InvalidOAuthStateException>(() => state.Verify(signed));

        // Assert
        Assert.Contains("no subject", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void rejects_a_state_signed_with_a_different_secret()
    {
        // Arrange — the signing key is derived per-server, so a state minted by
        // another deployment must not verify here.
        var theirs = Create(secret: "a-completely-different-access-secret-value-64").Issue(User);
        var ours = Create();

        // Act & Assert
        Assert.Throws<InvalidOAuthStateException>(() => ours.Verify(theirs));
    }

    [Fact]
    public void derives_the_signing_key_rather_than_using_the_access_secret()
    {
        // Arrange — the DOMAIN SEPARATION that stops a state token ever being
        // presented as a session token, or vice versa. If this ever collapsed to the
        // raw secret the two token families would share a key.
        var derived = GoogleOAuthState.DeriveSigningKey(Secret);

        // Assert
        Assert.Equal(32, derived.Length);
        Assert.NotEqual(Encoding.UTF8.GetBytes(Secret), derived);

        // The exact value node:crypto produces for
        // createHmac('sha256', SECRET).update('kitto:oauth-state:v1').digest().
        Assert.Equal(
            System.Security.Cryptography.HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(Secret),
                Encoding.UTF8.GetBytes("kitto:oauth-state:v1")),
            derived);
    }

    [Fact]
    public void writes_the_payload_keys_in_the_order_node_does()
    {
        // Arrange — uid, exp, n, then the optional w. Key order is not merely
        // cosmetic: it is what makes a state minted here byte-identical to one Node
        // would mint for the same inputs, which is what lets the two implementations
        // be cross-checked against a shared secret.
        var state = Create();

        // Act
        Assert.True(UrlSafeBase64.TryDecode(state.Issue(User, web: true).Split('.')[0], out var body));
        var json = Encoding.UTF8.GetString(body);

        // Assert
        Assert.StartsWith($"{{\"uid\":\"{User}\",\"exp\":", json, StringComparison.Ordinal);
        Assert.EndsWith(",\"w\":true}", json, StringComparison.Ordinal);
        Assert.Contains(",\"n\":\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void omits_the_web_flag_entirely_when_it_is_false()
    {
        // Arrange — Node spreads `...(web ? { w: true } : {})`, so the key is ABSENT
        // rather than false. A `"w":false` payload would still verify, but it would
        // not be the same bytes.
        var state = Create();

        // Act
        Assert.True(UrlSafeBase64.TryDecode(state.Issue(User).Split('.')[0], out var body));

        // Assert
        Assert.DoesNotContain("\"w\"", Encoding.UTF8.GetString(body), StringComparison.Ordinal);
    }

    private static string SignWith(string secret, string body)
    {
        var key = GoogleOAuthState.DeriveSigningKey(secret);
        var signature = UrlSafeBase64.Encode(
            System.Security.Cryptography.HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(body)));

        return $"{body}.{signature}";
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedClock(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
