using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;

namespace Life_Admin_Autopilot.DAL.Features.Auth;

/// <summary>
/// Port of the digest helpers in <c>server/src/lib/tokens.ts</c>.
///
/// <para>
/// <b>Two preimages, and they are not interchangeable.</b> A mailed LINK token
/// hashes the raw secret on its own. A 6-digit CODE hashes
/// <c>"&lt;userId&gt;:&lt;purpose&gt;:&lt;code&gt;"</c>, which binds it to one
/// account and one flow — so a code minted for user A is inert when replayed by
/// user B, and an email-change code cannot be redeemed as a verification code.
/// Using the link preimage for a code would silently make all six-digit codes
/// interchangeable across accounts.
/// </para>
///
/// <para>Digests are lowercase hex, matching Node's <c>.digest('hex')</c>.</para>
/// </summary>
public static class AuthTokenHashing
{
    /// <summary>Raw bytes of a refresh/link token: 32, rendered base64url as 43 chars.</summary>
    public const int SecretBytes = 32;

    /// <summary><c>sha256(raw)</c> — refresh tokens and every mailed link token.</summary>
    public static string HashToken(string raw) => Sha256Hex(raw);

    /// <summary>
    /// <c>sha256("&lt;userId&gt;:&lt;purpose&gt;:&lt;code&gt;")</c> — the 6-digit codes.
    /// <paramref name="userId"/> contributes its 24-hex string form, which is what
    /// Node's <c>String(user._id)</c> produces.
    /// </summary>
    public static string HashCode(ObjectId userId, string purpose, string code) =>
        Sha256Hex($"{userId}:{purpose}:{code}");

    /// <summary>
    /// 32 CSPRNG bytes as unpadded base64url — 43 characters matching
    /// <c>^[A-Za-z0-9_-]{43}$</c>. Used for refresh tokens and mailed link tokens.
    /// </summary>
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretBytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>
    /// Six digits, zero-padded. Node uses <c>randomInt(0, 10**6)</c> padded to
    /// width 6, so <c>"000042"</c> is a legal code and must not be normalised away.
    /// </summary>
    public static string NewSixDigitCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
