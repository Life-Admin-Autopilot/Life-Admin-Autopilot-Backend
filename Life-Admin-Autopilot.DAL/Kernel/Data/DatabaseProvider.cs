using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.DAL.Kernel.Data;

/// <summary>
/// Which SQL engine backs ASP.NET Identity.
///
/// <para>
/// The architecture decision is <b>where</b> data lives — credentials in Identity,
/// profile in Mongo — not which engine backs Identity. EF Core abstracts the
/// engine, and Identity's schema and refresh-token rotation port unchanged. This is
/// a <b>seam</b>, not a store switch: it reverts with one config value.
/// </para>
///
/// <para>
/// <b>Why it exists.</b> The default connection string is
/// <c>Server=(localdb)\mssqllocaldb;…</c>, and LocalDB is Windows-only. On macOS
/// there is no SQL Server to reach, so the moment any endpoint touches Identity
/// every test fails. SQLite unblocks local development and CI without changing the
/// production topology.
/// </para>
///
/// <para><b>Default is <see cref="SqlServer"/>.</b> Nothing changes unless a config
/// value says so.</para>
/// </summary>
public static class DatabaseProvider
{
    public const string SqlServer = "SqlServer";
    public const string Sqlite = "Sqlite";

    /// <summary>Config key: <c>Database:Provider</c>.</summary>
    public const string ConfigKey = "Database:Provider";

    /// <summary>
    /// Set <c>Database:EnsureCreated</c> to override. Defaults to true for SQLite and
    /// <b>always false for SQL Server</b> — see <see cref="ApplyTo"/>.
    /// </summary>
    public const string EnsureCreatedKey = "Database:EnsureCreated";

    /// <summary>Fallback SQLite file when no connection string names one.</summary>
    public const string DefaultSqliteConnection = "Data Source=life-admin-autopilot.db";

    public static string Resolve(IConfiguration configuration) =>
        configuration[ConfigKey] is { Length: > 0 } value ? value : SqlServer;

    public static bool IsSqlite(IConfiguration configuration) =>
        string.Equals(Resolve(configuration), Sqlite, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// EnsureCreated is used for SQLite ONLY, and never for SQL Server — production
    /// applies the checked-in migrations, and EnsureCreated would bypass them and
    /// leave a database migrations can never subsequently touch.
    /// </summary>
    public static bool ShouldEnsureCreated(IConfiguration configuration) =>
        IsSqlite(configuration) && configuration.GetValue(EnsureCreatedKey, true);

    /// <summary>
    /// Configures the <see cref="DbContextOptionsBuilder"/> for the selected engine.
    ///
    /// <para>
    /// <b>Migrations are provider-specific and SQL Server is the canonical target.</b>
    /// <c>20260724022126_InitialIdentity</c> was generated for SQL Server and
    /// production must keep applying it. Never regenerate migrations while
    /// <c>Database:Provider=Sqlite</c> — a SQLite-shaped migration overwriting the
    /// canonical set would break the production deploy. SQLite gets its schema from
    /// <c>EnsureCreated()</c> instead, so the two never share a migration history.
    /// </para>
    /// </summary>
    public static void ApplyTo(DbContextOptionsBuilder options, IConfiguration configuration)
    {
        var provider = Resolve(configuration);

        if (string.Equals(provider, Sqlite, StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlite(ResolveSqliteConnection(configuration));
            return;
        }

        if (string.Equals(provider, SqlServer, StringComparison.OrdinalIgnoreCase))
        {
            options.UseSqlServer(configuration.GetConnectionString("DefaultConnection"));
            return;
        }

        throw new InvalidOperationException(
            $"Unknown {ConfigKey} '{provider}'. Expected '{SqlServer}' or '{Sqlite}'.");
    }

    /// <summary>
    /// Resolution order, deliberately forgiving: an explicit SQLite string wins;
    /// otherwise <c>DefaultConnection</c> is used only if it actually looks like a
    /// SQLite DSN (so a leftover LocalDB string produces a clear fallback rather
    /// than a baffling driver error); otherwise a local file.
    /// </summary>
    public static string ResolveSqliteConnection(IConfiguration configuration)
    {
        if (configuration.GetConnectionString("SqliteConnection") is { Length: > 0 } explicitDsn)
        {
            return explicitDsn;
        }

        var fallback = configuration.GetConnectionString("DefaultConnection");
        if (fallback is { Length: > 0 } && fallback.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
        {
            return fallback;
        }

        return DefaultSqliteConnection;
    }
}
