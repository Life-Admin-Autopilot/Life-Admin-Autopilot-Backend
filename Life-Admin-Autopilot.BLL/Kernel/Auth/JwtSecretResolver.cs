using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Kernel.Auth;

/// <summary>
/// The one place the access-token secret is resolved from configuration.
///
/// <para>
/// Three keys, in this order, because three things need to keep working at once:
/// <c>Kernel:Jwt:AccessSecret</c> is the port's own setting and what the test
/// factory sets; <c>JWT_ACCESS_SECRET</c> is the environment variable the Node
/// reference reads, so a shared <c>.env</c> boots both servers; <c>Jwt:Key</c> is
/// the pre-existing .NET setting this repository had before the port, kept so an
/// existing environment still boots. See KERNEL.md §13.
/// </para>
///
/// <para>
/// <b>Plain <c>??</c>, deliberately.</b> The chain falls through on ABSENT keys
/// only — an explicitly empty value wins and short-circuits, exactly as the two
/// slice copies did. Do not "improve" this into a whitespace-aware check without
/// deciding what an empty secret should mean; today it means "configured as empty"
/// and fails closed at signing time rather than silently reaching for a different
/// key.
/// </para>
///
/// <para>
/// This existed as three identical inline copies — the auth slice's reader, the
/// Google slice's options reader (which inlined it rather than depend on the auth
/// slice mid-port), and the PL kernel's options <c>PostConfigure</c>. The first two
/// now call this. The third is deliberately NOT folded in: it runs after the
/// options binder has already consulted <c>Kernel:Jwt:AccessSecret</c> and guards on
/// <c>IsNullOrWhiteSpace</c> rather than null, so routing it through here would
/// change what a whitespace-only secret does. Noted rather than silently unified.
/// </para>
/// </summary>
public static class JwtSecretResolver
{
    public const string PortSettingKey = "Kernel:Jwt:AccessSecret";

    public const string NodeEnvironmentKey = "JWT_ACCESS_SECRET";

    public const string LegacySettingKey = "Jwt:Key";

    public static string Resolve(IConfiguration configuration) =>
        configuration[PortSettingKey]
        ?? configuration[NodeEnvironmentKey]
        ?? configuration[LegacySettingKey]
        ?? string.Empty;
}
