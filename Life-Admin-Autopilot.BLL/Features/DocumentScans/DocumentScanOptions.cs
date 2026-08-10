using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>
/// The five knobs <c>server/src/env.ts</c> exposes for document scans, with the
/// same defaults.
///
/// <para>
/// Each reads a structured key first and falls back to the Node environment
/// variable name, the same pattern the kernel uses for
/// <c>Kernel:Cors:Origins</c> / <c>CORS_ORIGINS</c> — so a single
/// <c>.env</c> can drive both servers during a differential.
/// </para>
/// </summary>
public sealed class DocumentScanOptions
{
    /// <summary>15 MiB. The FRIENDLY cap the handler enforces by hand.</summary>
    public int MaxBytes { get; init; } = 15 * 1024 * 1024;

    public int MaxPages { get; init; } = 20;

    public int FreeMonthlyQuota { get; init; } = 20;

    public int ProMonthlyQuota { get; init; } = 200;

    /// <summary>Local-disk root. Null means <c>&lt;cwd&gt;/uploads/document-scans</c>.</summary>
    public string? StorageDirectory { get; init; }

    /// <summary>
    /// The number that appears in the <c>payload_too_large</c> message. Node
    /// computes it as <c>Math.round(maxBytes / 1048576)</c>, so the message says
    /// "15MB" even though the limit is 15 MiB.
    /// </summary>
    public int MaxMegabytes => (int)Math.Round(MaxBytes / (1024.0 * 1024.0), MidpointRounding.AwayFromZero);

    public int QuotaFor(string tier) => tier == "pro" ? ProMonthlyQuota : FreeMonthlyQuota;

    public static DocumentScanOptions FromConfiguration(IConfiguration configuration) => new()
    {
        MaxBytes = ReadInt(configuration, "DocumentScans:MaxBytes", "DOCUMENT_SCAN_MAX_BYTES", 15 * 1024 * 1024),
        MaxPages = ReadInt(configuration, "DocumentScans:MaxPages", "DOCUMENT_SCAN_MAX_PAGES", 20),
        FreeMonthlyQuota = ReadInt(
            configuration, "DocumentScans:FreeMonthlyQuota", "DOCUMENT_SCAN_QUOTA_FREE_MONTHLY", 20),
        ProMonthlyQuota = ReadInt(
            configuration, "DocumentScans:ProMonthlyQuota", "DOCUMENT_SCAN_QUOTA_PRO_MONTHLY", 200),
        StorageDirectory = configuration["DocumentScans:StorageDirectory"]
                           ?? configuration["DOCUMENT_SCAN_STORAGE_DIR"],
    };

    /// <summary>Resolved local-disk root, matching Node's <c>join(cwd, 'uploads', 'document-scans')</c>.</summary>
    public string ResolveStorageRoot() =>
        string.IsNullOrWhiteSpace(StorageDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), "uploads", "document-scans")
            : StorageDirectory;

    private static int ReadInt(IConfiguration configuration, string key, string envKey, int fallback)
    {
        var raw = configuration[key] ?? configuration[envKey];
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : fallback;
    }
}
