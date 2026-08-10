using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The startup guard on the HS256 signing secret.
///
/// <para>
/// Before this existed, <c>appsettings.json</c> shipped
/// <c>Jwt:Key = "REPLACE_WITH_A_STRONG_SECRET_STORED_IN_USER_SECRETS"</c> and every
/// reader took it with a null-forgiving <c>!</c>. A deployment that forgot the
/// environment override booted and signed access tokens with a string published in
/// the repository, so anyone could mint a token for any <c>sub</c>. These tests pin
/// the three ways that secret can be unusable and the fact that each one is fatal.
/// </para>
/// </summary>
public class JwtSecretStartupTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    private const string GoodSecret = "kernel-test-access-secret-at-least-32-chars-long";

    [Fact]
    public void Validate_accepts_a_secret_of_at_least_32_bytes()
    {
        var exception = Record.Exception(
            () => KernelJwtSecret.Validate(Config(("Kernel:Jwt:AccessSecret", GoodSecret))));

        Assert.Null(exception);
    }

    [Fact]
    public void Validate_rejects_an_absent_secret()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => KernelJwtSecret.Validate(Config()));

        Assert.Contains("no JWT signing secret is configured", error.Message);
    }

    [Fact]
    public void Validate_rejects_an_empty_secret()
    {
        // The `??` chain stops at a configured empty string rather than falling
        // through it, so an explicit "" must be caught here and not resolve onward.
        var error = Assert.Throws<InvalidOperationException>(
            () => KernelJwtSecret.Validate(Config(
                ("Kernel:Jwt:AccessSecret", ""),
                ("Jwt:Key", GoodSecret))));

        Assert.Contains("no JWT signing secret is configured", error.Message);
    }

    [Fact]
    public void Validate_rejects_the_placeholder_shipped_in_appsettings()
    {
        // Long enough to clear the length check, so only the placeholder rule can
        // be what rejects it.
        var error = Assert.Throws<InvalidOperationException>(
            () => KernelJwtSecret.Validate(Config(
                ("Jwt:Key", "REPLACE_WITH_A_STRONG_SECRET_STORED_IN_USER_SECRETS"))));

        Assert.Contains("placeholder", error.Message);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789012345678901234567890")]  // 31 bytes: one under the bar
    public void Validate_rejects_a_secret_too_short_for_HS256(string secret)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => KernelJwtSecret.Validate(Config(("Kernel:Jwt:AccessSecret", secret))));

        Assert.Contains("HS256 requires at least 32", error.Message);
    }

    [Fact]
    public void Validate_counts_bytes_not_characters()
    {
        // 16 astral-plane characters: 16 chars in a C# string's terms, 64 UTF-8
        // bytes. Counting characters would reject a perfectly good key; counting
        // UTF-16 units would accept a 31-byte one. Encoding.UTF8 is the signer's
        // own measure, so it has to be ours.
        var emoji = string.Concat(Enumerable.Repeat("\U0001F510", 8));  // 8 x 4 bytes
        Assert.Equal(16, emoji.Length);
        Assert.Equal(32, System.Text.Encoding.UTF8.GetByteCount(emoji));

        var exception = Record.Exception(
            () => KernelJwtSecret.Validate(Config(("Kernel:Jwt:AccessSecret", emoji))));

        Assert.Null(exception);
    }

    [Fact]
    public void Resolve_prefers_the_kernel_key_then_the_node_env_var_then_Jwt_Key()
    {
        Assert.Equal("kernel", KernelJwtSecret.Resolve(Config(
            ("Kernel:Jwt:AccessSecret", "kernel"),
            ("JWT_ACCESS_SECRET", "node"),
            ("Jwt:Key", "legacy"))));

        Assert.Equal("node", KernelJwtSecret.Resolve(Config(
            ("JWT_ACCESS_SECRET", "node"),
            ("Jwt:Key", "legacy"))));

        Assert.Equal("legacy", KernelJwtSecret.Resolve(Config(("Jwt:Key", "legacy"))));

        Assert.Equal(string.Empty, KernelJwtSecret.Resolve(Config()));
    }

    [Fact]
    public async Task A_host_configured_with_the_placeholder_refuses_to_serve()
    {
        // Set on the winning key, so the result does not depend on whether the
        // machine running the suite happens to export JWT_ACCESS_SECRET.
        using var factory = new KernelWebApplicationFactory()
            .With("Kernel:Jwt:AccessSecret", "REPLACE_WITH_A_STRONG_SECRET_STORED_IN_USER_SECRETS");

        var error = await Record.ExceptionAsync(async () =>
        {
            using var client = factory.CreateApiClient();
            await client.GetAsync("/health");
        });

        Assert.NotNull(error);
        Assert.Contains("Refusing to start", Flatten(error!));
    }

    [Fact]
    public async Task A_host_configured_with_an_undersized_secret_refuses_to_serve()
    {
        using var factory = new KernelWebApplicationFactory()
            .With("Kernel:Jwt:AccessSecret", "too-short");

        var error = await Record.ExceptionAsync(async () =>
        {
            using var client = factory.CreateApiClient();
            await client.GetAsync("/health");
        });

        Assert.NotNull(error);
        Assert.Contains("Refusing to start", Flatten(error!));
    }

    /// <summary>The host wraps startup failures, so assert against the whole chain.</summary>
    private static string Flatten(Exception exception)
    {
        var text = string.Empty;
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            text += current.Message + "\n";
        }

        return text;
    }
}
