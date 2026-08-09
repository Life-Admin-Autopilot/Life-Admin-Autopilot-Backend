using Life_Admin_Autopilot.DAL.Data;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Kernel.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The Identity provider seam. The point of these is that the DEFAULT is unchanged
/// and that SQLite genuinely carries Identity — not that SQLite is now the store.
/// </summary>
public sealed class DatabaseProviderTests
{
    [Fact]
    public void defaults_to_sql_server_when_nothing_is_configured()
    {
        // Assert — the seam must be inert unless explicitly switched.
        Assert.Equal(DatabaseProvider.SqlServer, DatabaseProvider.Resolve(Config()));
        Assert.False(DatabaseProvider.IsSqlite(Config()));
    }

    [Fact]
    public void never_ensures_created_on_sql_server()
    {
        // Assert — EnsureCreated would bypass the canonical migrations and leave a
        // database migrations can never touch. Not even an explicit opt-in enables it.
        Assert.False(DatabaseProvider.ShouldEnsureCreated(Config()));
        Assert.False(DatabaseProvider.ShouldEnsureCreated(
            Config((DatabaseProvider.EnsureCreatedKey, "true"))));
    }

    [Fact]
    public void ensures_created_on_sqlite_unless_opted_out()
    {
        // Assert
        Assert.True(DatabaseProvider.ShouldEnsureCreated(
            Config((DatabaseProvider.ConfigKey, "Sqlite"))));

        Assert.False(DatabaseProvider.ShouldEnsureCreated(Config((DatabaseProvider.ConfigKey, "Sqlite"), (DatabaseProvider.EnsureCreatedKey, "false"))));
    }

    [Fact]
    public void rejects_an_unknown_provider_loudly()
    {
        // Assert — a typo must not silently fall back to a working engine.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DatabaseProvider.ApplyTo(
                new DbContextOptionsBuilder(),
                Config((DatabaseProvider.ConfigKey, "Postgres"))));

        Assert.Contains("Postgres", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ignores_a_leftover_localdb_string_when_running_on_sqlite()
    {
        // Arrange — the shipped default is a LocalDB DSN. Handing that to UseSqlite
        // would produce a baffling driver error.
        var config = Config((DatabaseProvider.ConfigKey, "Sqlite"), ("ConnectionStrings:DefaultConnection", @"Server=(localdb)\mssqllocaldb;Database=X;"));

        // Assert
        Assert.Equal(DatabaseProvider.DefaultSqliteConnection, DatabaseProvider.ResolveSqliteConnection(config));
    }

    [Fact]
    public void prefers_an_explicit_sqlite_connection_string()
    {
        // Arrange
        var config = Config((DatabaseProvider.ConfigKey, "Sqlite"), ("ConnectionStrings:SqliteConnection", "Data Source=explicit.db"), ("ConnectionStrings:DefaultConnection", "Data Source=fallback.db"));

        // Assert
        Assert.Equal("Data Source=explicit.db", DatabaseProvider.ResolveSqliteConnection(config));
    }

    [Fact]
    public async Task identity_round_trips_a_user_on_sqlite()
    {
        // Arrange — the claim that matters: Identity's schema and password hashing
        // work unchanged on SQLite, so the seam really does unblock auth.
        var path = Path.Combine(Path.GetTempPath(), $"kitto-provider-{Guid.NewGuid():N}.db");
        var config = Config(
            (DatabaseProvider.ConfigKey, "Sqlite"),
            ("ConnectionStrings:SqliteConnection", $"Data Source={path}"));

        var services = new ServiceCollection();
        services.AddLogging();

        // AddDefaultTokenProviders needs IDataProtectionProvider, which the web host
        // supplies in the real app but a bare ServiceCollection does not.
        services.AddDataProtection();
        services.AddDbContext<ApplicationDbContext>(o => DatabaseProvider.ApplyTo(o, config));
        services.AddIdentityCore<ApplicationUser>(o =>
            {
                o.Password.RequiredLength = 8;
                o.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        try
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();

            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = "kernel@probe.com", Email = "kernel@probe.com" };

            // Act — note the complexity: Identity's DEFAULT options demand an
            // uppercase letter and a non-alphanumeric character. Node's signup schema
            // is only `z.string().min(8).max(128)`, so the auth slice must relax
            // IdentityOptions.Password to reach parity. This test is about the SQLite
            // seam, so it uses a policy-compliant password rather than asserting the
            // policy.
            var created = await users.CreateAsync(user, "Password123!");

            // Assert
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

            var found = await users.FindByEmailAsync("kernel@probe.com");
            Assert.NotNull(found);
            Assert.True(await users.CheckPasswordAsync(found!, "Password123!"));
            Assert.False(await users.CheckPasswordAsync(found!, "wrong-password"));
        }
        finally
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Database.EnsureDeletedAsync();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}
