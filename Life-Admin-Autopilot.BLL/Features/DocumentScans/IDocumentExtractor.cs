using Life_Admin_Autopilot.DAL.Kernel.Errors;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>One candidate the vision pass proposed, before it is given a key.</summary>
public sealed record DraftCandidate(
    string Title,
    string Domain,
    string Priority,
    string Confidence,
    DateTime? DueAt = null,
    string? Notes = null,
    int? SourcePage = null,
    int? EstimateMinMinutes = null,
    int? EstimateMaxMinutes = null);

/// <summary>Everything one extraction pass produces.</summary>
public sealed record DocumentExtraction(
    IReadOnlyList<DraftCandidate> Candidates,
    string? DocumentSummary = null,
    string? DocumentType = null,
    string? DocumentTitle = null,
    string? DocumentSubtitle = null,
    string? Issuer = null);

public sealed record DocumentExtractionRequest(
    byte[] Bytes,
    string MimeType,
    string? Timezone,
    string? Locale);

/// <summary>
/// The AI seam. <c>modules/ai/documentCore/extract.ts</c> lives behind this, and
/// the document-scan slice deliberately knows nothing about Gemini.
/// </summary>
public interface IDocumentExtractor
{
    Task<DocumentExtraction> ExtractAsync(
        DocumentExtractionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The no-key implementation, and the CURRENT PARITY TARGET.
///
/// <para>
/// Node's <c>getGeminiClient()</c> throws <c>503 ai_not_configured</c> the moment
/// extraction is attempted without <c>GEMINI_API_KEY</c>, so the reference server
/// genuinely fails every scan: the worker's retry classifier treats 503 as
/// transient, burns the whole attempt ladder, and the document settles at
/// <c>failed</c> with this exact sentence in <c>failureReason</c>. Reproducing
/// that honestly is the job — a stub that fabricated candidates would green a
/// screen the reference server cannot produce.
/// </para>
///
/// <para>
/// The AI slice REPLACES this registration (<c>services.Replace(...)</c>, not
/// <c>TryAdd</c>) when a real provider lands.
/// </para>
/// </summary>
public sealed class NullDocumentExtractor : IDocumentExtractor
{
    /// <summary>Verbatim from <c>modules/ai/provider/geminiClient.ts</c>.</summary>
    public const string NotConfiguredMessage =
        "AI is not configured. Set GEMINI_API_KEY in server/.env to enable.";

    public Task<DocumentExtraction> ExtractAsync(
        DocumentExtractionRequest request,
        CancellationToken cancellationToken = default) =>
        throw new AppException(503, "ai_not_configured", NotConfiguredMessage);
}
