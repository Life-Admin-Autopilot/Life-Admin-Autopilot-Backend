using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.DocumentScans;

/// <summary>
/// Reads an uploaded page and proposes the matters it implies.
///
/// <para>
/// The implementation <see cref="NullDocumentExtractor"/> has been waiting for.
/// Registered only when a key is present, and by <c>services.Replace</c> — the
/// null one is a <c>TryAdd</c>, so adding this without replacing would silently
/// leave every scan failing.
/// </para>
///
/// <para>
/// <b>One call, whole document.</b> The bytes go up as <c>inline_data</c> exactly as
/// stored — no page splitting, no OCR pass first. Gemini reads PDFs natively, and
/// splitting would cost one request per page against a free tier that allows twenty
/// a day, while losing the cross-page context that tells the model a due date on
/// page three belongs to the bill on page one.
/// </para>
///
/// <para>
/// <b>Nothing here saves anything.</b> Candidates land in <c>candidates</c> for the
/// user to accept or discard, which is why an imperfect read is recoverable and the
/// prompt can afford to propose rather than agonise.
/// </para>
/// </summary>
public sealed class GeminiDocumentExtractor : IDocumentExtractor
{
    /// <summary>The frontend's enums. A candidate outside them is unrenderable.</summary>
    private static readonly string[] Domains = { "health", "home", "car", "finance", "family", "pets" };

    private static readonly string[] Priorities = { "low", "normal", "high", "urgent" };

    private static readonly string[] Confidences = { "high", "medium", "low" };

    private static readonly string[] DocumentTypes =
    {
        "bill", "statement", "letter", "form", "receipt",
        "insurance", "medical", "legal", "identity", "tax", "other",
    };

    /// <summary>
    /// What the provider will actually accept as <c>inline_data</c>. Anything else
    /// is rejected here rather than sent, so the failure names the real problem
    /// instead of surfacing a provider 400 the user cannot act on.
    /// </summary>
    private static readonly string[] SupportedMimeTypes =
    {
        "application/pdf",
        "image/png", "image/jpeg", "image/webp", "image/heic", "image/heif",
    };

    private readonly HttpClient _http;
    private readonly DocumentExtractionOptions _options;
    private readonly ILogger<GeminiDocumentExtractor> _logger;

    public GeminiDocumentExtractor(
        HttpClient http,
        DocumentExtractionOptions options,
        ILogger<GeminiDocumentExtractor> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<DocumentExtraction> ExtractAsync(
        DocumentExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
        {
            // Belt and braces: the registration already gates on this, so reaching
            // here means the graph was built by hand.
            throw new AppException(503, "ai_not_configured", NullDocumentExtractor.NotConfiguredMessage);
        }

        var mime = (request.MimeType ?? string.Empty).Trim().ToLowerInvariant();
        if (!SupportedMimeTypes.Contains(mime))
        {
            // 415, deliberately: the retry classifier treats 429/500/503/504 as
            // transient, and an unreadable file will still be unreadable in a minute.
            // Retrying it would spend the whole ladder to reach the same answer.
            throw new AppException(
                415,
                "unsupported_document_type",
                $"This file type cannot be read ({mime}). Upload a PDF or a photo.");
        }

        var payload = new GeminiRequest(
            new[]
            {
                new GeminiContent(new object[]
                {
                    new GeminiInlinePart(new GeminiBlob(mime, Convert.ToBase64String(request.Bytes))),
                    new GeminiTextPart("Read this document and reply with the JSON object described."),
                }),
            },
            new GeminiSystem(new[] { new GeminiTextPart(BuildPrompt(request.Timezone, request.Locale)) }),
            new GeminiConfig(0.2, 4096, "application/json"));

        var body = await SendAsync(payload, cancellationToken).ConfigureAwait(false);
        return Parse(body);
    }

    private static string BuildPrompt(string? timezone, string? locale)
    {
        var now = DateTimeOffset.UtcNow;
        var zone = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone;
        var language = string.IsNullOrWhiteSpace(locale) ? "the document's own language" : locale;

        return
            "You read a scanned or photographed personal document and report what it is, "
            + "plus the actions it obliges its owner to take.\n"
            + $"Current UTC time: {now:o}. The owner's timezone: {zone}.\n"
            + "Reply with ONLY a JSON object, no prose and no code fence:\n"
            + "{\"documentType\": one of bill|statement|letter|form|receipt|insurance|medical|legal|identity|tax|other,\n"
            + " \"documentTitle\": short name for this document, e.g. \"Electricity bill\",\n"
            + " \"documentSubtitle\": account number, period or reference, or null,\n"
            + " \"issuer\": the organisation that produced it, or null,\n"
            + " \"documentSummary\": one or two sentences on what it says,\n"
            + " \"totalAmount\": the headline sum printed on the page as a plain number "
            + "(1234.56, no separators, no symbol), or null,\n"
            + " \"currency\": ISO 4217 code for that sum, e.g. USD, EGP, EUR, JPY, or null,\n"
            + " \"direction\": \"out\" if the owner pays it, \"in\" if they receive it "
            + "(a refund, rebate or credit), or null,\n"
            + " \"amountDueAt\": ISO-8601 date the sum falls due, or the date it was paid "
            + "on a receipt, or null,\n"
            + " \"candidates\": [ {\"title\":string, "
            + "\"domain\":one of health|home|car|finance|family|pets, "
            + "\"priority\":one of low|normal|high|urgent, "
            + "\"confidence\":one of high|medium|low, "
            + "\"dueAt\":ISO-8601 with offset or null, \"notes\":string|null, "
            + "\"sourcePage\":1-based page the item came from or null, "
            + "\"estimateMinMinutes\":number|null, \"estimateMaxMinutes\":number|null, "
            + "\"amount\":the sum THIS action costs as a plain number or null, "
            + "\"currency\":ISO 4217 code for it or null} ] }\n"
            // The same failure the planning prompt had to be tightened against: a
            // title translated out of the document's own words is a matter the user
            // cannot find by searching for what they read on the page.
            + $"Write documentTitle, documentSummary and every candidate title in {language}. "
            + "Never translate names, reference numbers or the issuer.\n"
            + "Rules for candidates: one per distinct action the owner must take — a payment "
            + "due, a renewal, an appointment to book, a form to return. A document that "
            + "obliges nothing gets an EMPTY candidates array; do not invent an action to "
            + "fill it. Only set dueAt from a date PRINTED on the document, never a guess. "
            + "confidence is how certain you are the document really asks for that action.\n"
            // Money is the highest-consequence thing on the page: a wrong figure is
            // read as a fact about the user's own account, and it is arithmetic they
            // will trust rather than re-check. Every rule here buys a null over a
            // guess.
            + "Rules for money: copy figures EXACTLY as printed — never convert between "
            + "currencies, never add tax, never sum several lines into a total the page "
            + "does not itself state. totalAmount is the one headline figure a person "
            + "would read off the document (amount due, total, balance); if the page "
            + "shows several and none is the headline, return null. A candidate's amount "
            + "is what that single action costs, and it is null unless the page ties a "
            + "figure to that action — do not reuse totalAmount for it. If you cannot "
            + "tell which currency a figure is in, return null for BOTH the figure and "
            + "the currency: an amount with the wrong currency is worse than no amount.";
    }

    // ---- Transport --------------------------------------------------------
    //
    // The same chain walk as PlanningService, for the same measured reason: free-tier
    // capacity flaps per model, so a 503/429 is an answer about ONE model and the next
    // is worth trying immediately, while any other status is about the request itself
    // and would fail identically everywhere.

    private async Task<string> SendAsync(GeminiRequest payload, CancellationToken cancellationToken)
    {
        foreach (var model in _options.ModelChain)
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(_options.AttemptTimeoutSeconds));

            string body;
            HttpStatusCode statusCode;

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, _options.GenerateUriFor(model))
                {
                    Content = JsonContent.Create(payload),
                };
                message.Headers.Add("x-goog-api-key", _options.ApiKey);

                using var response = await _http.SendAsync(message, attempt.Token).ConfigureAwait(false);
                body = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
                statusCode = response.StatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException
                                       && !cancellationToken.IsCancellationRequested)
            {
                // A hang is the commonest shape of "this model is unavailable" and has
                // to walk the chain like the 503 it would have been. The caller's own
                // cancellation is excluded: a shutting-down worker is not a model failure.
                _logger.LogWarning(
                    "documentScan:model-unreachable model={Model} reason={Reason}",
                    model,
                    ex.GetType().Name);
                continue;
            }

            if (statusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
            {
                if (!string.Equals(model, _options.Model, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("documentScan:used-fallback model={Model}", model);
                }

                return body;
            }

            var status = (int)statusCode;
            _logger.LogWarning(
                "documentScan:model-failed model={Model} status={Status} body={Body}",
                model,
                status,
                body.Length > 300 ? body[..300] : body);

            if (status is not (503 or 429)) break;
        }

        // 503 so the worker's classifier treats it as transient and the document is
        // retried rather than settled at failed — the provider being busy is exactly
        // the case the attempt ladder exists for.
        throw new AppException(
            503,
            "document_ai_unavailable",
            "The document reader is unavailable right now. Try again in a moment.");
    }

    // ---- Parsing ----------------------------------------------------------

    private DocumentExtraction Parse(string body)
    {
        string? raw;
        try
        {
            raw = JsonDocument.Parse(body)
                .RootElement.GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0]
                .GetProperty("text").GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "documentScan:unrecognised-model-response");
            throw new AppException(502, "document_ai_bad_response", "The document reader returned an unusable response.");
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            // An empty answer is usually a safety block on someone's ID or medical
            // page. Nothing actionable, but the scan still succeeded — settling it at
            // ready_for_review with no candidates beats a failure the user cannot fix.
            _logger.LogWarning("documentScan:empty-model-response");
            return new DocumentExtraction(Array.Empty<DraftCandidate>());
        }

        // responseMimeType asks for bare JSON, but a fallback model that ignores it
        // fences anyway; strip rather than fail.
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('\n');
            var end = json.LastIndexOf("```", StringComparison.Ordinal);
            if (start >= 0 && end > start) json = json[(start + 1)..end].Trim();
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException ex)
        {
            _logger.LogError(
                ex,
                "documentScan:model-returned-non-json payload={Payload}",
                json[..Math.Min(300, json.Length)]);
            throw new AppException(502, "document_ai_bad_response", "The document reader returned an unusable response.");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return new DocumentExtraction(Array.Empty<DraftCandidate>());
        }

        var drafts = new List<DraftCandidate>();
        if (root.TryGetProperty("candidates", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in items.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var title = Str(element, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                drafts.Add(new DraftCandidate(
                    title.Trim(),
                    OneOf(Str(element, "domain"), Domains, "home"),
                    OneOf(Str(element, "priority"), Priorities, "normal"),
                    OneOf(Str(element, "confidence"), Confidences, "medium"),
                    Date(element, "dueAt"),
                    Str(element, "notes"),
                    Int(element, "sourcePage"),
                    Int(element, "estimateMinMinutes"),
                    Int(element, "estimateMaxMinutes"),
                    // A candidate inherits the document's direction: a refund
                    // letter's one action is receiving the refund.
                    MoneyVocabulary.Normalize(
                        Decimal(element, "amount"),
                        Str(element, "currency"),
                        "ai",
                        Str(root, "direction"))));
            }
        }

        return new DocumentExtraction(
            drafts,
            DocumentSummary: Str(root, "documentSummary"),
            DocumentType: OneOf(Str(root, "documentType"), DocumentTypes, "other"),
            DocumentTitle: Str(root, "documentTitle"),
            DocumentSubtitle: Str(root, "documentSubtitle"),
            Issuer: Str(root, "issuer"),
            Amount: MoneyVocabulary.Normalize(
                Decimal(root, "totalAmount"),
                Str(root, "currency"),
                "ai",
                Str(root, "direction")),
            AmountDueAt: Date(root, "amountDueAt"));
    }

    /// <summary>
    /// The model is told the enums but is not bound by them, and an out-of-vocabulary
    /// value renders as an unknown chip. Falling back beats discarding a good candidate.
    /// </summary>
    private static string OneOf(string? value, string[] allowed, string fallback) =>
        value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : fallback;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s
            : null;

    /// <summary>
    /// A money figure as the model returned it.
    ///
    /// <para>
    /// A string is accepted as well as a number because models routinely quote
    /// numerics they consider precise — and money is exactly the case they treat
    /// that way. Parsed INVARIANT so "1234.56" is one thousand and not one and a
    /// bit; the prompt asks for no separators, and a comma-grouped string that
    /// slips through fails to parse and becomes a null rather than a 100x error.
    /// </para>
    /// </summary>
    private static decimal? Decimal(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var parsed) => parsed,
            JsonValueKind.String when decimal.TryParse(
                v.GetString(),
                System.Globalization.NumberStyles.AllowDecimalPoint
                    | System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out var text) => text,
            _ => null,
        };
    }

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static DateTime? Date(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String
        && DateTime.TryParse(
            v.GetString(),
            null,
            System.Globalization.DateTimeStyles.AdjustToUniversal
                | System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    // ---- Wire shapes ------------------------------------------------------
    //
    // Parts are object[] because one request mixes an inline blob with a text
    // instruction, and Gemini's part is a union rather than a record with both.

    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("systemInstruction")] GeminiSystem SystemInstruction,
        [property: JsonPropertyName("generationConfig")] GeminiConfig GenerationConfig);

    private sealed record GeminiContent([property: JsonPropertyName("parts")] object[] Parts);

    private sealed record GeminiSystem([property: JsonPropertyName("parts")] GeminiTextPart[] Parts);

    private sealed record GeminiTextPart([property: JsonPropertyName("text")] string Text);

    private sealed record GeminiInlinePart([property: JsonPropertyName("inline_data")] GeminiBlob InlineData);

    private sealed record GeminiBlob(
        [property: JsonPropertyName("mime_type")] string MimeType,
        [property: JsonPropertyName("data")] string Data);

    private sealed record GeminiConfig(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
        [property: JsonPropertyName("responseMimeType")] string ResponseMimeType);
}
