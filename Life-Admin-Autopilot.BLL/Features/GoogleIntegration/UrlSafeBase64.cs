using System.Text;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>
/// Node's <c>base64url</c> Buffer encoding: standard base64 with <c>+</c> → <c>-</c>,
/// <c>/</c> → <c>_</c>, and <b>no padding</b>.
///
/// <para>
/// Both the OAuth state token and the token-cipher payload are defined in that
/// alphabet, and both are compared or parsed byte-for-byte against values Node
/// produced, so <c>Convert.ToBase64String</c> alone (which pads with <c>=</c> and
/// uses the URL-hostile alphabet) is not interchangeable.
/// </para>
/// </summary>
public static class UrlSafeBase64
{
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string EncodeUtf8(string value) => Encode(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Decode, or return false. Node's Buffer decoder is lenient where .NET's is
    /// strict, but every caller here treats an undecodable value as a rejection, so
    /// the two agree on the OUTCOME even where they disagree on the mechanism —
    /// Node produces garbage that then fails a JSON parse or a length check.
    /// </summary>
    public static bool TryDecode(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value is null)
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,

            // A length of 1 mod 4 cannot be produced by any base64 encoder.
            _ => padded + "===",
        };

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
