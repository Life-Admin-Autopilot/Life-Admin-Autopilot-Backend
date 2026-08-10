using System.Security.Cryptography;
using Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

namespace Life_Admin_Autopilot.Tests.Features.GoogleIntegration;

/// <summary>
/// Encryption of third-party tokens at rest. <b>Unreachable from the parity
/// harness</b> — nothing is ever encrypted on a server with no
/// <c>INTEGRATION_ENCRYPTION_KEY</c> — and a silent weakness here is permanent read
/// access to every connected user's calendar, so it is unit tested directly.
///
/// <para>Mirrors <c>lib/tokenCipher.test.ts</c>.</para>
/// </summary>
public sealed class GoogleTokenCipherTests
{
    private const string Token = "1//0eXaMpLe-refresh-token_with.punctuation";

    /// <summary>64 hex characters, exactly as <c>env.ts</c> demands.</summary>
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static GoogleTokenCipher Create(string? key = Key) =>
        new(new GoogleIntegrationOptions { EncryptionKey = key });

    [Fact]
    public void reports_configured_only_when_a_key_is_present()
    {
        // Arrange & Act & Assert — the routes branch on this to answer
        // `available:false` instead of throwing deep inside a request.
        Assert.True(Create().IsConfigured);
        Assert.False(Create(null).IsConfigured);
        Assert.False(Create(string.Empty).IsConfigured);
    }

    [Fact]
    public void round_trips_a_token()
    {
        // Arrange
        var cipher = Create();

        // Act & Assert
        Assert.Equal(Token, cipher.Decrypt(cipher.Encrypt(Token)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("مرحبا")]
    [InlineData("🔐 emoji")]
    public void round_trips_unicode_and_empty_strings(string value)
    {
        // Arrange
        var cipher = Create();

        // Act & Assert
        Assert.Equal(value, cipher.Decrypt(cipher.Encrypt(value)));
    }

    [Fact]
    public void round_trips_a_long_value()
    {
        // Arrange
        var cipher = Create();
        var value = new string('a', 4096);

        // Act & Assert
        Assert.Equal(value, cipher.Decrypt(cipher.Encrypt(value)));
    }

    [Fact]
    public void produces_different_ciphertext_each_time()
    {
        // Arrange — a fresh random IV per encryption. Reusing a nonce under GCM is
        // catastrophic: it leaks the XOR of two plaintexts and allows forging the auth
        // tag. Identical output for identical input would be the visible symptom.
        var cipher = Create();

        // Act
        var a = cipher.Encrypt(Token);
        var b = cipher.Encrypt(Token);

        // Assert
        Assert.NotEqual(a, b);
        Assert.Equal(cipher.Decrypt(a), cipher.Decrypt(b));
    }

    [Fact]
    public void never_stores_the_plaintext_in_the_payload()
    {
        // Arrange & Act & Assert
        Assert.DoesNotContain("refresh-token", Create().Encrypt(Token), StringComparison.Ordinal);
    }

    [Fact]
    public void writes_the_versioned_four_segment_form()
    {
        // Arrange — `v1.<iv>.<tag>.<ciphertext>`, base64url. The version prefix is
        // what will let a key rotation teach decrypt about `v2` while `v1` rows stay
        // readable.
        var cipher = Create();

        // Act
        var parts = cipher.Encrypt(Token).Split('.');

        // Assert
        Assert.Equal(4, parts.Length);
        Assert.Equal("v1", parts[0]);
        Assert.True(UrlSafeBase64.TryDecode(parts[1], out var iv));
        Assert.Equal(12, iv.Length);
        Assert.True(UrlSafeBase64.TryDecode(parts[2], out var tag));
        Assert.Equal(16, tag.Length);

        // base64url: no padding, and neither of the two URL-hostile characters.
        Assert.DoesNotContain('=', cipher.Encrypt(Token));
        Assert.DoesNotContain('+', cipher.Encrypt(Token));
        Assert.DoesNotContain('/', cipher.Encrypt(Token));
    }

    [Fact]
    public void rejects_tampered_ciphertext()
    {
        // Arrange — the whole reason for GCM over CBC: an attacker with write access
        // to the database must not be able to flip bits and have us decrypt their
        // choice of garbage without noticing.
        var cipher = Create();
        var parts = cipher.Encrypt(Token).Split('.');
        Assert.True(UrlSafeBase64.TryDecode(parts[3], out var ciphertext));
        ciphertext[0] ^= 0xff;
        parts[3] = UrlSafeBase64.Encode(ciphertext);

        // Act & Assert
        Assert.Throws<DecryptionFailedException>(() => cipher.Decrypt(string.Join('.', parts)));
    }

    [Fact]
    public void rejects_a_tampered_auth_tag()
    {
        // Arrange
        var cipher = Create();
        var parts = cipher.Encrypt(Token).Split('.');
        Assert.True(UrlSafeBase64.TryDecode(parts[2], out var tag));
        tag[0] ^= 0xff;
        parts[2] = UrlSafeBase64.Encode(tag);

        // Act & Assert
        Assert.Throws<DecryptionFailedException>(() => cipher.Decrypt(string.Join('.', parts)));
    }

    [Fact]
    public void rejects_a_tampered_iv()
    {
        // Arrange
        var cipher = Create();
        var parts = cipher.Encrypt(Token).Split('.');
        Assert.True(UrlSafeBase64.TryDecode(parts[1], out var iv));
        iv[0] ^= 0xff;
        parts[1] = UrlSafeBase64.Encode(iv);

        // Act & Assert
        Assert.Throws<DecryptionFailedException>(() => cipher.Decrypt(string.Join('.', parts)));
    }

    [Fact]
    public void rejects_ciphertext_written_under_a_different_key()
    {
        // Arrange — the case that drives the row to `needs_reauth` rather than
        // retrying: the encryption key changed, and no amount of retrying fixes it.
        var payload = Create().Encrypt(Token);
        var other = Create("f".PadLeft(64, 'f'));

        // Act & Assert
        Assert.Throws<DecryptionFailedException>(() => other.Decrypt(payload));
    }

    [Theory]
    [InlineData("")]
    [InlineData("v1")]
    [InlineData("v1.aaa.bbb")]
    [InlineData("v1.aaa.bbb.ccc.ddd")]
    public void rejects_a_malformed_payload(string payload)
    {
        // Arrange & Act
        var error = Assert.Throws<DecryptionFailedException>(() => Create().Decrypt(payload));

        // Assert
        Assert.Contains("malformed payload", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void rejects_an_unknown_version()
    {
        // Arrange
        var parts = Create().Encrypt(Token).Split('.');
        parts[0] = "v2";

        // Act
        var error = Assert.Throws<DecryptionFailedException>(() => Create().Decrypt(string.Join('.', parts)));

        // Assert
        Assert.Contains("unknown version v2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void rejects_a_missing_iv_or_tag_but_not_an_empty_ciphertext()
    {
        // Arrange — encrypting "" produces zero ciphertext bytes and the auth tag
        // still proves it was us. So the segment guard cannot be one truthiness check
        // across all three.
        var cipher = Create();
        var empty = cipher.Encrypt(string.Empty);
        var parts = empty.Split('.');

        // Act & Assert
        Assert.Equal(string.Empty, parts[3]);
        Assert.Equal(string.Empty, cipher.Decrypt(empty));

        Assert.Contains(
            "missing segment",
            Assert.Throws<DecryptionFailedException>(() => cipher.Decrypt($"v1..{parts[2]}.{parts[3]}")).Message,
            StringComparison.Ordinal);

        Assert.Contains(
            "missing segment",
            Assert.Throws<DecryptionFailedException>(() => cipher.Decrypt($"v1.{parts[1]}..{parts[3]}")).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void rejects_an_iv_of_the_wrong_length()
    {
        // Arrange — 96 bits is the only nonce size GCM uses directly rather than
        // hashing down.
        var cipher = Create();
        var parts = cipher.Encrypt(Token).Split('.');
        parts[1] = UrlSafeBase64.Encode(new byte[16]);

        // Act
        var error = Assert.Throws<DecryptionFailedException>(() => cipher.Decrypt(string.Join('.', parts)));

        // Assert
        Assert.Contains("bad iv length", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void throws_rather_than_encrypting_with_no_key()
    {
        // Arrange — callers gate on IsConfigured; reaching here anyway must fail
        // loudly rather than storing something unencrypted. The payload is
        // well-formed so the decrypt reaches the key lookup rather than tripping the
        // shape guards first, which is the order Node's key() sits in too.
        var cipher = Create(null);
        var wellFormed = Create().Encrypt(Token);

        // Act & Assert
        Assert.Throws<EncryptionNotConfiguredException>(() => cipher.Encrypt(Token));
        Assert.Throws<EncryptionNotConfiguredException>(() => cipher.Decrypt(wellFormed));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef")]
    public void refuses_a_key_that_is_not_32_bytes_of_hex(string key)
    {
        // Arrange — a hex string with an odd character silently decodes SHORT rather
        // than failing, and a 20-byte "AES-256" key is a weakness nothing else would
        // report.
        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => Create(key));
    }

    [Fact]
    public void interoperates_with_a_payload_built_the_way_node_builds_one()
    {
        // Arrange — assembled here with the raw primitives in Node's field order, to
        // prove the stored FORMAT is compatible and not merely self-consistent.
        var key = Convert.FromHexString(Key);
        var iv = new byte[12];
        var plaintext = System.Text.Encoding.UTF8.GetBytes(Token);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using (var aes = new AesGcm(key, 16))
        {
            aes.Encrypt(iv, plaintext, ciphertext, tag);
        }

        var payload = string.Join(
            '.',
            "v1",
            UrlSafeBase64.Encode(iv),
            UrlSafeBase64.Encode(tag),
            UrlSafeBase64.Encode(ciphertext));

        // Act & Assert
        Assert.Equal(Token, Create().Decrypt(payload));
    }
}
