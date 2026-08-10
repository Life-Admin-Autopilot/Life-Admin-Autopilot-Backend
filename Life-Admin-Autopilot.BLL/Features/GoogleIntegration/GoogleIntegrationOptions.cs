using Life_Admin_Autopilot.BLL.Kernel.Auth;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>
/// Everything the Google slice reads from configuration, resolved once at
/// registration.
///
/// <para>
/// Each value falls back to the Node environment-variable name so a single
/// <c>server/.env</c>-shaped environment configures both servers identically. The
/// <b>parity target on the reference server is that none of them is set</b>, which
/// is what makes <c>available:false</c> / <c>400 google_not_configured</c> the
/// verified Phase 1 behaviour rather than a stub.
/// </para>
/// </summary>
public sealed class GoogleIntegrationOptions
{
    public string? ClientId { get; init; }

    public string? ClientSecret { get; init; }

    public string? RedirectUri { get; init; }

    /// <summary>64 hex characters. Absent means connected accounts are unavailable.</summary>
    public string? EncryptionKey { get; init; }

    /// <summary>
    /// <c>APP_DEEP_LINK_SCHEME</c>, zod-defaulted to <c>kitto</c>. Every callback exit
    /// is built from it.
    /// </summary>
    public string DeepLinkScheme { get; init; } = "kitto";

    /// <summary>
    /// The access-token secret. NOT used to sign anything directly — the OAuth state
    /// key is DERIVED from it (see <see cref="GoogleOAuthState"/>).
    /// </summary>
    public string AccessSecret { get; init; } = string.Empty;

    /// <summary>
    /// <c>isGoogleConfigured()</c> — a truthiness check over the three OAuth values,
    /// so an empty string counts as absent exactly as it does in Node.
    /// </summary>
    public bool IsGoogleConfigured =>
        !string.IsNullOrEmpty(ClientId)
        && !string.IsNullOrEmpty(ClientSecret)
        && !string.IsNullOrEmpty(RedirectUri);

    public static GoogleIntegrationOptions Read(IConfiguration configuration) => new()
    {
        ClientId = configuration["Google:ClientId"] ?? configuration["GOOGLE_CLIENT_ID"],
        ClientSecret = configuration["Google:ClientSecret"] ?? configuration["GOOGLE_CLIENT_SECRET"],
        RedirectUri = configuration["Google:RedirectUri"] ?? configuration["GOOGLE_REDIRECT_URI"],
        EncryptionKey = configuration["Integrations:EncryptionKey"]
            ?? configuration["INTEGRATION_ENCRYPTION_KEY"],
        DeepLinkScheme = Truthy(configuration["App:DeepLinkScheme"])
            ?? Truthy(configuration["APP_DEEP_LINK_SCHEME"])
            ?? "kitto",

        // KERNEL.md §13's three-key chain. This was inlined during the port so the
        // slice compiled on its own branch without depending on the auth slice; it
        // now shares the kernel resolver, which belongs to neither slice.
        AccessSecret = JwtSecretResolver.Resolve(configuration),
    };

    private static string? Truthy(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
