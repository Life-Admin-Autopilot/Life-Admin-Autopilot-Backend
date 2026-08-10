using System.Text;

namespace Life_Admin_Autopilot_Backend.Kernel.Auth;

/// <summary>
/// The HS256 signing secret: one place that resolves it, and one guard that refuses
/// to boot without a usable one.
///
/// <para>
/// Four readers resolve this secret independently — <see cref="KernelAuthOptions"/>
/// (verify), <c>AuthJwtConfiguration.Read</c> (sign), <c>GoogleIntegrationOptions</c>
/// (OAuth state) and <c>AddJwtAuthentication</c>. They all walk the same three-key
/// chain, so validating the chain once at startup covers every one of them.
/// </para>
///
/// <para><b>Why this has to be fatal.</b> <c>appsettings.json</c> used to ship
/// <c>Jwt:Key</c> as a literal placeholder, and every reader took it with a
/// null-forgiving <c>!</c>. A deployment that forgot the environment override booted
/// happily and signed access tokens with a value published in the repository —
/// anyone reading it could mint a token for any <c>sub</c>. A server that cannot sign
/// safely must not serve, so the placeholder is gone from configuration and an
/// unusable secret now stops startup instead of silently degrading it.</para>
/// </summary>
public static class KernelJwtSecret
{
    /// <summary>
    /// HS256 keys shorter than the 32-byte hash output add no security over a
    /// 32-byte one and are what RFC 7518 §3.2 requires as a minimum.
    /// </summary>
    public const int MinimumBytes = 32;

    /// <summary>
    /// The convention <c>appsettings.json</c> uses for "you must override this".
    /// Any secret still carrying it is a public string, not a secret.
    /// </summary>
    private const string PlaceholderPrefix = "REPLACE_WITH";

    /// <summary>
    /// The three-key chain, in the order every other reader walks it:
    /// <c>Kernel:Jwt:AccessSecret</c>, then Node's <c>JWT_ACCESS_SECRET</c> env var,
    /// then the pre-existing .NET <c>Jwt:Key</c>. Kept byte-identical to those
    /// readers — including the <c>??</c> semantics, which stop at a configured
    /// empty string rather than falling through it.
    /// </summary>
    public static string Resolve(IConfiguration configuration) =>
        configuration["Kernel:Jwt:AccessSecret"]
        ?? configuration["JWT_ACCESS_SECRET"]
        ?? configuration["Jwt:Key"]
        ?? string.Empty;

    /// <summary>
    /// Throws unless the resolved secret is present, is not the shipped placeholder,
    /// and is long enough for HS256. Called from <c>UseKernel()</c>, which runs after
    /// configuration is final and before Kestrel binds a port.
    /// </summary>
    /// <exception cref="InvalidOperationException">The secret is unusable.</exception>
    public static void Validate(IConfiguration configuration)
    {
        var secret = Resolve(configuration);

        if (string.IsNullOrWhiteSpace(secret))
        {
            throw Fatal("no JWT signing secret is configured");
        }

        if (secret.TrimStart().StartsWith(PlaceholderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Fatal(
                "the JWT signing secret is still the placeholder shipped in appsettings.json, " +
                "which is a public string");
        }

        var bytes = Encoding.UTF8.GetByteCount(secret);
        if (bytes < MinimumBytes)
        {
            throw Fatal(
                $"the JWT signing secret is {bytes} bytes; HS256 requires at least {MinimumBytes}");
        }
    }

    private static InvalidOperationException Fatal(string reason) => new(
        $"Refusing to start: {reason}. Tokens signed with an absent, placeholder or " +
        "undersized key can be forged by anyone, for any user. Set one of " +
        "'Kernel:Jwt:AccessSecret', 'JWT_ACCESS_SECRET' or 'Jwt:Key' to at least " +
        $"{MinimumBytes} bytes of random data — user secrets in development, the " +
        "environment or a secret manager in deployment. It must match the Node " +
        "server's JWT_ACCESS_SECRET for the two to accept each other's tokens.");
}
