using Life_Admin_Autopilot_Backend;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// In-process host for endpoint tests.
///
/// <para>
/// <b>Every slice tests its own endpoints through this.</b> Derive from it, or use
/// it directly and override configuration per test class. Before it existed the
/// Tests project referenced only BLL and could not reach a controller at all.
/// </para>
///
/// <para><b>What it changes, and why</b></para>
/// <list type="bullet">
///   <item>
///     Points Mongo at the parity instance and the <c>kitto_parity_dotnet</c>
///     database. <b>Never the Atlas cluster</b> — that is production data.
///   </item>
///   <item>
///     Disables rate limiters. Node does the same under <c>NODE_ENV=test</c>, so a
///     suite can hit an endpoint repeatedly without tripping a counter. A test that
///     is specifically ABOUT rate limiting re-enables them (see
///     <see cref="WithRateLimiting"/>).
///   </item>
///   <item>
///     Disables background workers, so a factory per test class does not start five
///     pollers each.
///   </item>
///   <item>
///     Pins a CORS allowlist and a JWT secret, so those tests do not depend on a
///     developer's local <c>.env</c>.
///   </item>
/// </list>
/// </summary>
public class KernelWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string ParityMongoUri = "mongodb://127.0.0.1:27018";
    public const string ParityDatabase = "kitto_parity_dotnet";

    /// <summary>Matches <c>server/.env</c>'s <c>CORS_ORIGINS</c> shape.</summary>
    public const string AllowedOrigin = "http://localhost:3000";

    public const string TestJwtSecret = "kernel-test-access-secret-at-least-32-chars-long";

    private readonly Dictionary<string, string?> _overrides = new();

    /// <summary>
    /// A private SQLite file per factory, so no two test classes share Identity rows
    /// and a failed run cannot poison the next. Deleted on dispose.
    /// </summary>
    private readonly string _sqlitePath =
        Path.Combine(Path.GetTempPath(), $"kitto-tests-{Guid.NewGuid():N}.db");

    /// <summary>Add or replace a configuration value before the host is created.</summary>
    public KernelWebApplicationFactory With(string key, string? value)
    {
        _overrides[key] = value;
        return this;
    }

    /// <summary>Turn the limiters back on for a test that is specifically about them.</summary>
    public KernelWebApplicationFactory WithRateLimiting() => With("Kernel:RateLimit:Enabled", "true");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Environments.Production);

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var settings = new Dictionary<string, string?>
            {
                ["MongoDbSettings:ConnectionString"] = ParityMongoUri,
                ["MongoDbSettings:DatabaseName"] = ParityDatabase,

                // Identity on SQLite. The shipped default is LocalDB, which is
                // Windows-only — without this, the first test that touches Identity
                // fails on macOS and Linux. Production keeps SqlServer.
                ["Database:Provider"] = "Sqlite",
                ["ConnectionStrings:SqliteConnection"] = $"Data Source={_sqlitePath}",
                ["Kernel:Cors:Origins"] = $"{AllowedOrigin},capacitor://localhost,http://localhost",
                ["Kernel:Jwt:AccessSecret"] = TestJwtSecret,
                ["Kernel:RateLimit:Enabled"] = "false",
                ["Kernel:Workers:Enabled"] = "false",
                ["Kernel:UseHttpsRedirection"] = "false",
            };

            foreach (var (key, value) in _overrides)
            {
                settings[key] = value;
            }

            config.AddInMemoryCollection(settings);
        });
    }

    /// <summary>
    /// A client that does NOT follow redirects — a 3xx is a parity failure, not
    /// something to transparently chase.
    /// </summary>
    public HttpClient CreateApiClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
        {
            return;
        }

        try
        {
            if (File.Exists(_sqlitePath))
            {
                File.Delete(_sqlitePath);
            }
        }
        catch (IOException)
        {
            // A leftover temp file is harmless; failing a suite over it is not.
        }
    }
}
