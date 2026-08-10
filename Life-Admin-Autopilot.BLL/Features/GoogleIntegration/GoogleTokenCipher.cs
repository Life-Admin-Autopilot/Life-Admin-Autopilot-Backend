using System.Security.Cryptography;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>Thrown when the encryption key is absent. Mirrors <c>EncryptionNotConfiguredError</c>.</summary>
public sealed class EncryptionNotConfiguredException : Exception
{
    public EncryptionNotConfiguredException()
        : base("INTEGRATION_ENCRYPTION_KEY is not set — connected accounts are unavailable.")
    {
    }
}

/// <summary>Thrown when stored ciphertext cannot be read. Mirrors <c>DecryptionFailedError</c>.</summary>
public sealed class DecryptionFailedException : Exception
{
    public DecryptionFailedException(string reason)
        : base($"Stored credential could not be read: {reason}")
    {
    }
}

/// <summary>
/// Authenticated encryption for third-party tokens at rest.
/// Port of <c>server/src/lib/tokenCipher.ts</c>.
/// </summary>
public interface IGoogleTokenCipher
{
    /// <summary>
    /// Whether token storage is usable at all. Mirrors <c>isTokenCipherConfigured()</c>:
    /// the server boots without a key so local development and the test suite do not
    /// need one, and the integration routes answer with a clear error instead of
    /// throwing deep inside a request.
    /// </summary>
    bool IsConfigured { get; }

    string Encrypt(string plaintext);

    string Decrypt(string payload);
}

/// <summary>
/// AES-256-GCM, which is AUTHENTICATED. Not CBC, not raw CTR: without the auth tag
/// an attacker with write access to the database could flip ciphertext bits and we
/// would decrypt attacker-chosen garbage without noticing.
///
/// <para>
/// The stored form is versioned — <c>v1.&lt;iv&gt;.&lt;tag&gt;.&lt;ciphertext&gt;</c>,
/// each segment base64url — so a key rotation can be rolled out by teaching decrypt
/// about <c>v2</c> while <c>v1</c> rows are still readable. Rotation itself is not
/// implemented, but the format will not have to be redesigned when it is.
/// </para>
/// </summary>
public sealed class GoogleTokenCipher : IGoogleTokenCipher
{
    private const string Version = "v1";

    /// <summary>
    /// 96 bits is the GCM-recommended nonce length: the only size the spec uses
    /// directly rather than hashing down, and what every audited implementation
    /// expects.
    /// </summary>
    private const int IvBytes = 12;

    private const int KeyBytes = 32;
    private const int TagBytes = 16;

    private readonly byte[]? _key;

    public GoogleTokenCipher(GoogleIntegrationOptions options)
    {
        _key = ReadKey(options.EncryptionKey);
    }

    public bool IsConfigured => _key is not null;

    public string Encrypt(string plaintext)
    {
        var key = RequireKey();
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(iv, plainBytes, ciphertext, tag);

        return string.Join(
            '.',
            Version,
            UrlSafeBase64.Encode(iv),
            UrlSafeBase64.Encode(tag),
            UrlSafeBase64.Encode(ciphertext));
    }

    public string Decrypt(string payload)
    {
        var parts = payload.Split('.');
        if (parts.Length != 4)
        {
            throw new DecryptionFailedException("malformed payload");
        }

        var (version, ivPart, tagPart, ctPart) = (parts[0], parts[1], parts[2], parts[3]);
        if (version != Version)
        {
            throw new DecryptionFailedException($"unknown version {version}");
        }

        // The ciphertext segment may legitimately be EMPTY — encrypting "" produces
        // zero bytes, and the auth tag still proves it was us who produced them. Only
        // the IV and tag must be present, so this cannot be one truthiness check
        // across all three.
        if (ivPart.Length == 0 || tagPart.Length == 0)
        {
            throw new DecryptionFailedException("missing segment");
        }

        if (!UrlSafeBase64.TryDecode(ivPart, out var iv)
            || !UrlSafeBase64.TryDecode(tagPart, out var tag)
            || !UrlSafeBase64.TryDecode(ctPart, out var ciphertext))
        {
            throw new DecryptionFailedException("authentication failed");
        }

        if (iv.Length != IvBytes)
        {
            throw new DecryptionFailedException("bad iv length");
        }

        if (tag.Length != TagBytes)
        {
            // Node lets node:crypto's setAuthTag reject this inside the try, which
            // surfaces as the same 'authentication failed'.
            throw new DecryptionFailedException("authentication failed");
        }

        var key = RequireKey();
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(iv, ciphertext, tag, plaintext);
        }
        catch (CryptographicException)
        {
            // GCM's tag check failed: the ciphertext was altered or the key changed.
            // Never a recoverable state, and never something to paper over by
            // returning a partial plaintext.
            throw new DecryptionFailedException("authentication failed");
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    private byte[] RequireKey() => _key ?? throw new EncryptionNotConfiguredException();

    /// <summary>
    /// Length is validated here as well as at configuration time because a hex
    /// string with an odd character silently decodes SHORT rather than failing — and
    /// a 20-byte "AES-256" key is a weakness nothing else would report.
    /// </summary>
    private static byte[]? ReadKey(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        byte[] parsed;
        try
        {
            parsed = Convert.FromHexString(raw);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                $"INTEGRATION_ENCRYPTION_KEY must decode to {KeyBytes} bytes of hex.");
        }

        if (parsed.Length != KeyBytes)
        {
            throw new InvalidOperationException(
                $"INTEGRATION_ENCRYPTION_KEY must decode to {KeyBytes} bytes of hex.");
        }

        return parsed;
    }
}
