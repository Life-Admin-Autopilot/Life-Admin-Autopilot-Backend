using Life_Admin_Autopilot.BLL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Features.DocumentScans;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.Quota;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.Tests.Features.DocumentScans;

/// <summary>
/// The pieces that decide how long a scan takes to fail, and how big a scan may
/// be. No database and no HTTP — these are pure functions with observable
/// consequences.
/// </summary>
public sealed class DocumentScanRetryPolicyTests
{
    [Fact]
    public void treats_ai_not_configured_as_transient()
    {
        // This single classification is why an unconfigured server takes ~20s to
        // settle a scan instead of failing on the first attempt. The reference
        // server does the same, so the delay is contract, not a bug.
        var notConfigured = new AppException(503, "ai_not_configured", NullDocumentExtractor.NotConfiguredMessage);

        Assert.True(DocumentScanRetryPolicy.IsTransient(notConfigured));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(504)]
    public void treats_the_four_provider_overload_statuses_as_transient(int status) =>
        Assert.True(DocumentScanRetryPolicy.IsTransient(new AppException(status, "x", "y")));

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    public void treats_a_client_error_as_terminal(int status) =>
        Assert.False(DocumentScanRetryPolicy.IsTransient(new AppException(status, "x", "a plain refusal")));

    [Theory]
    [InlineData("Model is OVERLOADED right now")]
    [InlineData("service unavailable")]
    [InlineData("please try again later")]
    [InlineData("rate limit exceeded")]
    public void falls_back_to_the_message_when_there_is_no_status(string message) =>
        Assert.True(DocumentScanRetryPolicy.IsTransient(new InvalidOperationException(message)));

    [Theory]
    [InlineData(1, 2_000)]
    [InlineData(2, 4_000)]
    [InlineData(3, 8_000)]
    [InlineData(4, 16_000)]
    public void doubles_the_backoff_each_attempt(int attempts, int expectedFloorMs)
    {
        var delay = DocumentScanRetryPolicy.Backoff(attempts, new Random(1));

        // Floor plus up to a second of jitter.
        Assert.InRange(delay.TotalMilliseconds, expectedFloorMs, expectedFloorMs + 1_000);
    }

    [Fact]
    public void caps_the_backoff_and_never_overflows()
    {
        // The shift is what overflows if it is written naively; 1000 attempts is not
        // reachable in practice but the arithmetic must still be sane.
        foreach (var attempts in new[] { 20, 64, 1_000 })
        {
            var delay = DocumentScanRetryPolicy.Backoff(attempts, new Random(1));
            Assert.InRange(delay.TotalMilliseconds, 60_000, 61_000);
        }
    }
}

/// <summary>The env-backed knobs and the 402 payload they produce.</summary>
public sealed class DocumentScanOptionsTests
{
    [Fact]
    public void defaults_match_the_node_env_schema()
    {
        var options = DocumentScanOptions.FromConfiguration(Config());

        Assert.Equal(15 * 1024 * 1024, options.MaxBytes);
        Assert.Equal(20, options.MaxPages);
        Assert.Equal(20, options.FreeMonthlyQuota);
        Assert.Equal(200, options.ProMonthlyQuota);
    }

    [Fact]
    public void reports_15MB_for_the_friendly_cap_message()
    {
        // Node: Math.round(maxBytes / 1048576). The limit is 15 MiB and the message
        // says "15MB" — the imprecision is part of the string.
        Assert.Equal(15, DocumentScanOptions.FromConfiguration(Config()).MaxMegabytes);
    }

    [Fact]
    public void reads_the_node_environment_variable_names_as_a_fallback()
    {
        var options = DocumentScanOptions.FromConfiguration(Config(
            ("DOCUMENT_SCAN_MAX_BYTES", "1048576"),
            ("DOCUMENT_SCAN_MAX_PAGES", "3"),
            ("DOCUMENT_SCAN_QUOTA_FREE_MONTHLY", "5")));

        Assert.Equal(1024 * 1024, options.MaxBytes);
        Assert.Equal(3, options.MaxPages);
        Assert.Equal(5, options.FreeMonthlyQuota);
        Assert.Equal(1, options.MaxMegabytes);
    }

    [Fact]
    public void prefers_the_structured_key_over_the_environment_variable()
    {
        var options = DocumentScanOptions.FromConfiguration(Config(
            ("DocumentScans:MaxPages", "7"),
            ("DOCUMENT_SCAN_MAX_PAGES", "3")));

        Assert.Equal(7, options.MaxPages);
    }

    [Fact]
    public void picks_the_pro_ceiling_only_for_the_pro_tier()
    {
        var options = DocumentScanOptions.FromConfiguration(Config());

        Assert.Equal(200, options.QuotaFor("pro"));
        Assert.Equal(20, options.QuotaFor("free"));

        // Anything unrecognised is treated as free — the same `tier === 'pro'` test
        // Node makes, so an unknown tier can never buy the larger allowance.
        Assert.Equal(20, options.QuotaFor("enterprise"));
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();
}

/// <summary>The reset instant that ships inside the quota meter.</summary>
public sealed class DocumentScanQuotaResetTests
{
    [Theory]
    [InlineData("2026-08", "2026-09-01T00:00:00.000Z")]
    [InlineData("2026-01", "2026-02-01T00:00:00.000Z")]
    [InlineData("2026-12", "2027-01-01T00:00:00.000Z")]
    public void rolls_to_midnight_utc_on_the_first_of_the_next_month(string month, string expected) =>
        Assert.Equal(expected, UsageQuotaBuckets.NextMonthStartIso(month));
}

/// <summary>The three notification bodies, which are literal strings.</summary>
public sealed class DocumentScanNotificationCopyTests
{
    [Theory]
    [InlineData(0, "We didn't find anything actionable in that scan.")]
    [InlineData(1, "1 item found — take a look.")]
    [InlineData(2, "2 items found — take a look.")]
    [InlineData(9, "9 items found — take a look.")]
    public void pluralises_exactly_as_the_reference_does(int count, string expected) =>
        Assert.Equal(expected, DocumentScanNotifications.BodyFor(count));
}

/// <summary>The storage key layout, shared by the writer and the eraser.</summary>
public sealed class DocumentScanStorageKeyTests
{
    [Theory]
    [InlineData("application/pdf", "pdf")]
    [InlineData("image/jpeg", "jpg")]
    [InlineData("image/png", "png")]
    [InlineData("image/heic", "heic")]
    [InlineData("image/webp", "webp")]
    [InlineData("application/octet-stream", "bin")]
    public void names_the_blob_after_the_owner_the_scan_and_the_format(string mime, string extension) =>
        Assert.Equal($"user1/scan1.{extension}", DocumentScanStorageKeys.Build("user1", "scan1", mime));
}
