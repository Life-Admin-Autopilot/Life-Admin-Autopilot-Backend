using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Features.Planning;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.Digest;

/// <summary>
/// The one thing in the digest a language model is allowed to write: the sentence at
/// the top of the home screen.
///
/// <para>
/// <b>The line is drawn at prose, and it is drawn hard.</b> Every figure in the
/// payload is computed from documents by <see cref="DailyDigestComputer"/>; this
/// class is handed the matters that were already read and asked only to say what the
/// day is. It returns a STRING or null — it cannot touch a count, and a failure here
/// leaves <see cref="NeutralHeadline"/>'s sentence in place. A number that came out
/// of a language model is a surface for confident wrong answers the user cannot
/// check, and this one is on screen the moment they open the app.
/// </para>
///
/// <para>
/// <b>Nothing here throws.</b> By the time it runs the digest is already complete,
/// correct and served; giving up something real because the decorative layer was
/// unavailable is the wrong trade.
/// </para>
///
/// <para>
/// Reaches the model through <see cref="PlanningOptions"/> — the same Google
/// credential and the same fallback chain the proposal extractor walks. Deliberately
/// NOT the Langflow provider: that is a streaming, tool-calling agent loop, and this
/// is one stateless sentence.
/// </para>
/// </summary>
public sealed class DigestProseWriter
{
    /// <summary>
    /// Longer than this is not a headline. A model that returns three paragraphs has
    /// misunderstood the job, and the hero has room for roughly two lines.
    /// </summary>
    private const int MaxHeadlineLength = 240;

    /// <summary>
    /// How many matters the model is shown. The pool is already capped at 120 by the
    /// computer; past a couple of dozen the sentence stops being about anything in
    /// particular, and the ones that matter are the ones due soonest — the pool
    /// arrives sorted by deadline, so this takes the front of it.
    /// </summary>
    private const int MaxMattersShown = 24;

    private readonly HttpClient _http;
    private readonly PlanningOptions _options;
    private readonly ILogger<DigestProseWriter> _logger;

    public DigestProseWriter(
        HttpClient http,
        PlanningOptions options,
        ILogger<DigestProseWriter> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Whether there is a model to ask at all. Read by the digest service BEFORE
    /// queueing, so a server with no key never claims a sentence is coming and the
    /// dashboard never polls for one.
    /// </summary>
    public bool IsConfigured => _options.IsConfigured;

    /// <summary>The day's sentence, or null if the model could not produce a usable one.</summary>
    public async Task<string?> WriteAsync(DigestProseJob job, CancellationToken cancellationToken)
    {
        if (!IsConfigured || job.Pool.Count == 0)
        {
            return null;
        }

        var request = new GeminiRequest(
            new[] { new GeminiContent(new[] { new GeminiPart(UserMessage(job)) }) },
            new GeminiSystem(new[] { new GeminiPart(SystemFor(job.Locale)) }),

            // Low, not zero. The sentence should read as written rather than
            // templated, but it is describing facts and must not wander off them.
            //
            // The budget is generous relative to the 240-character ceiling because a
            // budget that binds does not shorten the sentence — it CUTS it, and a cut
            // sentence is what shipped. Thinking is off (see GeminiThinking), so this
            // is the answer's own allowance and nothing competes with it.
            new GeminiConfig(0.3, 800, new GeminiThinking(0)));

        foreach (var model in _options.ModelChain)
        {
            var text = await AskAsync(model, request, cancellationToken).ConfigureAwait(false);

            if (text is Attempt.Unavailable)
            {
                // 503/429 — this model is busy, the next one in the chain may not be.
                continue;
            }

            return text is Attempt.Answered answered ? Clean(answered.Text) : null;
        }

        _logger.LogWarning("daily-digest:prose-chain-exhausted localDate={LocalDate}", job.LocalDate);
        return null;
    }

    // ---- The prompt --------------------------------------------------------

    private const string SystemBase = """
        You write the single sentence at the top of a personal admin app's home screen.
        It is the first thing the person reads when they open the app.

        You are given their matters as one per line:
          title · domain · STATE

        STATE is one of OVERDUE by Nd / DUE TODAY / DUE in Nd, and it is ALWAYS
        authoritative. Never infer a matter's state from words in its title — a matter
        called "Pay overdue vet bill" marked DUE in 3d is NOT overdue; it is a bill about
        an overdue account, due later. Getting this backwards writes a sentence that
        contradicts itself, which is worse than saying nothing.

        WHAT TO WRITE
        - One sentence. Two only if one genuinely will not carry it.
        - Say what the day actually holds, naming two or three real matters, worked into
          the sentence rather than listed or quoted. Their titles are the words to use.
        - Address the person directly. "Today you're booking the North Coast tickets and
          chasing the airline refund, with a new pet carrier to order."
        - When there is more than you can name, name the two or three that come first and
          close the sentence with a plain count of the rest.
        - Never invent a matter, a date, an amount or a count. Only what you were given.
        - Never imply they are behind, late or failing. No "still", "finally", "already".
          Do not count what they have not done.
        - No cheerleading, no exclamation marks, no markdown, no bullet points, no
          surrounding quotation marks.
        - If you need a noun for them, they are "matters" — never "tasks" or "items".

        The ids are not shown to you and no identifier belongs in the sentence.

        Reply with ONLY the sentence itself. No preamble, no JSON, no formatting.
        """;

    /// <summary>
    /// The headline is the first thing on the home screen. Written in English under an
    /// Arabic layout it is not a rough edge — it is the app dropping the user's own
    /// language on the one surface nobody has to navigate to.
    /// </summary>
    private static string SystemFor(string locale) =>
        locale switch
        {
            "ar" => SystemBase + "\n\nWrite the sentence in Arabic.",
            _ => SystemBase + "\n\nWrite the sentence in English.",
        };

    private static string UserMessage(DigestProseJob job)
    {
        var builder = new StringBuilder();
        builder.Append("=== NOW ===\n");
        builder.Append(job.Now.ToString("O", CultureInfo.InvariantCulture));
        builder.Append("\n=== MATTERS ===\n");

        foreach (var matter in job.Pool.Take(MaxMattersShown))
        {
            builder.Append(Line(matter, job.Now));
            builder.Append('\n');
        }

        var hidden = job.Pool.Count - MaxMattersShown;
        if (hidden > 0)
        {
            // Named rather than silently dropped: a sentence that says "and eleven
            // others" when there are thirty is a wrong count, and the model can only
            // avoid it if it knows what it was not shown.
            builder.Append(CultureInfo.InvariantCulture, $"(and {hidden} further matters not listed)\n");
        }

        builder.Append("=== END ===");
        return builder.ToString();
    }

    /// <summary>
    /// One matter as the model sees it. The STATE is resolved HERE, in code, from the
    /// same clock the counts were computed against — never left for the model to work
    /// out from a raw timestamp.
    /// </summary>
    private static string Line(DigestPoolMatter matter, DateTime now)
    {
        var state = StateOf(matter.DueAt, now);
        var domain = string.IsNullOrWhiteSpace(matter.Domain) ? "general" : matter.Domain;
        return $"{JsText.CollapseWhitespace(JsText.Trim(matter.Title))} · {domain} · {state}";
    }

    private static string StateOf(DateTime? dueAt, DateTime now)
    {
        if (dueAt is not { } due)
        {
            return "NO DATE";
        }

        // Whole days between the two instants, which is what the sentence talks in.
        // Truncating rather than rounding keeps "DUE TODAY" meaning today: something
        // due in twenty hours is tomorrow's, not "in 1d" rounded up from now.
        var days = (int)(due.Date - now.Date).TotalDays;

        return days switch
        {
            < 0 => string.Create(CultureInfo.InvariantCulture, $"OVERDUE by {-days}d"),
            0 => "DUE TODAY",
            _ => string.Create(CultureInfo.InvariantCulture, $"DUE in {days}d"),
        };
    }

    // ---- The call ----------------------------------------------------------

    private abstract record Attempt
    {
        public sealed record Answered(string Text) : Attempt;

        /// <summary>503/429 — try the next model in the chain.</summary>
        public sealed record Unavailable : Attempt;

        /// <summary>Anything else. The chain stops; the plain headline stands.</summary>
        public sealed record Failed : Attempt;
    }

    private async Task<Attempt> AskAsync(
        string model,
        GeminiRequest request,
        CancellationToken cancellationToken)
    {
        // A per-attempt budget, not the whole walk's. Measured on the proposal path: a
        // hung model consumed the entire 60s and three healthy fallbacks were never
        // tried.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(_options.AttemptTimeoutSeconds));

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _options.GenerateUriFor(model))
            {
                Content = JsonContent.Create(request),
            };
            message.Headers.Add("x-goog-api-key", _options.ApiKey);

            using var response = await _http.SendAsync(message, attempt.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 503 or 429)
                {
                    return new Attempt.Unavailable();
                }

                _logger.LogWarning(
                    "daily-digest:prose-failed model={Model} status={Status}",
                    model,
                    (int)response.StatusCode);
                return new Attempt.Failed();
            }

            var body = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
            var text = ReadText(body);

            return text is null ? new Attempt.Failed() : new Attempt.Answered(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host is shutting down, not the model failing. Nothing to log and
            // nothing to fall back to.
            return new Attempt.Failed();
        }
        catch (OperationCanceledException)
        {
            // The per-attempt budget. A model that hangs is unavailable in every sense
            // that matters here, so the chain walks on.
            _logger.LogWarning("daily-digest:prose-timeout model={Model}", model);
            return new Attempt.Unavailable();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "daily-digest:prose-error model={Model}", model);
            return new Attempt.Failed();
        }
    }

    /// <summary>
    /// The candidate's text, or null if the envelope was not what it should be — or
    /// if the model did not finish the sentence.
    ///
    /// <para>
    /// <b><c>finishReason</c> is checked, and this is the whole reason the headline
    /// stopped mid-clause.</b> A candidate that ran out of budget still carries text,
    /// and that text is a fragment: "Today you need to pay the Concordia Hill
    /// Hospital" was served to real users under the greeting. <see cref="Clean"/>
    /// cannot catch it — its only length rule rejects text that is too LONG, and a
    /// truncated sentence is short. So the stop reason is the signal, and anything
    /// other than a clean stop is treated as no answer at all. Falling back to the
    /// computed sentence is never wrong; half a sentence always is.
    /// </para>
    /// </summary>
    private string? ReadText(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            var candidate = document.RootElement.GetProperty("candidates")[0];

            // MAX_TOKENS, SAFETY, RECITATION — all mean the same thing here: what
            // came back is not the sentence that was asked for. Absent is fine; some
            // responses omit it entirely and those are ordinary completions.
            if (candidate.TryGetProperty("finishReason", out var finish)
                && finish.GetString() is { } reason
                && !string.Equals(reason, "STOP", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("daily-digest:prose-unfinished reason={Reason}", reason);
                return null;
            }

            var text = candidate
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException)
        {
            _logger.LogWarning(ex, "daily-digest:prose-unparsable");
            return null;
        }
    }

    // ---- Cleaning ----------------------------------------------------------

    /// <summary>
    /// What the model returns is a suggestion, not a headline. It is asked for one
    /// bare sentence; it sometimes sends a fenced block, a leading "Headline:", or a
    /// sentence in quotes. Rather than trust the instruction, strip what does not
    /// belong and reject what is left if it is not usable — a null here is a clean
    /// fall back to the computed sentence.
    /// </summary>
    public static string? Clean(string raw)
    {
        var text = raw.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var start = text.IndexOf('\n');
            var end = text.LastIndexOf("```", StringComparison.Ordinal);
            text = start >= 0 && end > start ? text[(start + 1)..end].Trim() : text.Trim('`').Trim();
        }

        // Newlines are what a list looks like once the bullets are gone. The hero is
        // one paragraph, so they collapse rather than render.
        text = JsText.Trim(JsText.CollapseWhitespace(text.Replace('\n', ' ').Replace('\r', ' ')));

        // A sentence the model wrapped in quotes, not a quotation.
        if (text.Length >= 2 &&
            (text[0] == '"' || text[0] == '“') &&
            (text[^1] == '"' || text[^1] == '”'))
        {
            text = text[1..^1].Trim();
        }

        if (text.Length == 0 || text.Length > MaxHeadlineLength)
        {
            return null;
        }

        // A sentence that does not END is not a sentence.
        //
        // The stop reason already catches the usual truncation, but this is the check
        // that does not depend on the provider reporting one — a response envelope
        // that omits `finishReason`, or a future model that spells it differently,
        // would put a fragment straight onto the home screen. Terminal punctuation is
        // a weak signal in general and a sufficient one here, because the prompt asks
        // for exactly one finished sentence: anything trailing off mid-clause fails
        // it, and the computed headline takes over.
        if (!EndsLikeASentence(text))
        {
            return null;
        }

        return text;
    }

    /// <summary>
    /// Western and Arabic full stops, question and exclamation marks, and the
    /// ellipsis — an ellipsis is a deliberate authorial choice rather than a cut.
    /// </summary>
    private static bool EndsLikeASentence(string text) =>
        text[^1] is '.' or '!' or '?' or '…' or '۔' or '؟';

    // ---- Wire shapes -------------------------------------------------------

    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("systemInstruction")] GeminiSystem SystemInstruction,
        [property: JsonPropertyName("generationConfig")] GeminiConfig GenerationConfig);

    private sealed record GeminiContent([property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiSystem([property: JsonPropertyName("parts")] GeminiPart[] Parts);

    private sealed record GeminiPart([property: JsonPropertyName("text")] string Text);

    private sealed record GeminiConfig(
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
        [property: JsonPropertyName("thinkingConfig")] GeminiThinking Thinking);

    /// <summary>
    /// Thinking is switched OFF for this call, and that is the fix for the truncated
    /// headline rather than a performance tweak.
    ///
    /// <para>
    /// On a thinking-capable model the reasoning tokens are drawn from the SAME
    /// <c>maxOutputTokens</c> budget as the answer. With 256 for both, the model
    /// spent nearly all of it deliberating about a one-sentence summary and had ten
    /// tokens left to write it in — which is exactly how a headline stops after
    /// "Today you need to pay the Concordia Hill Hospital".
    /// </para>
    ///
    /// <para>
    /// Nothing here needs deliberation. The facts are computed, the pool is handed
    /// over pre-sorted and pre-labelled, and the job is one sentence of prose about
    /// them. Zero also makes the call cheaper and faster on a surface the user is
    /// waiting on.
    /// </para>
    /// </summary>
    private sealed record GeminiThinking(
        [property: JsonPropertyName("thinkingBudget")] int ThinkingBudget);
}
