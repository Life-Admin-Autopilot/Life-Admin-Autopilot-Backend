using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.Knowledge;

/// <summary>
/// Text → vector. One seam so the store, the ingest hop and the retrieval hop never
/// name a vendor.
/// </summary>
public interface IEmbeddingProvider
{
    bool IsConfigured { get; }

    string Model { get; }

    /// <summary>
    /// <paramref name="isQuery"/> selects the task type. Asymmetric embedding models
    /// place a QUESTION and the PASSAGE that answers it differently, and asking for
    /// the wrong side measurably degrades recall — so the ingest hop passes false and
    /// retrieval passes true, rather than both using one default.
    /// </summary>
    Task<float[]> EmbedAsync(string text, bool isQuery, CancellationToken cancellationToken = default);
}

/// <summary>
/// Google Generative Language <c>:embedContent</c>.
///
/// <para>
/// <b>Vectors are L2-normalised here.</b> At the default 3072 dimensions the model
/// returns unit vectors, but every truncated <c>outputDimensionality</c> — including
/// the 768 this slice asks for — comes back UN-normalised (measured: ‖v‖ ≈ 0.587).
/// Cosine similarity is scale-invariant so Atlas ranking survives either way, but
/// storing mixed-magnitude vectors makes any future dotProduct index silently wrong
/// and makes two chunks' scores incomparable. Normalising once, at the boundary, is
/// cheaper than remembering that everywhere downstream.
/// </para>
/// </summary>
public sealed class GeminiEmbeddingProvider : IEmbeddingProvider
{
    private readonly HttpClient _http;
    private readonly EmbeddingOptions _options;
    private readonly ILogger<GeminiEmbeddingProvider> _logger;

    public GeminiEmbeddingProvider(
        HttpClient http,
        EmbeddingOptions options,
        ILogger<GeminiEmbeddingProvider> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;

    public string Model => _options.Model;

    public async Task<float[]> EmbedAsync(
        string text,
        bool isQuery,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Embeddings are not configured. Set EMBEDDINGS_API_KEY.");
        }

        var request = new EmbedRequest(
            $"models/{_options.Model}",
            new EmbedContent(new[] { new EmbedPart(text) }),
            isQuery ? "RETRIEVAL_QUERY" : "RETRIEVAL_DOCUMENT",
            Dimensions);

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.EmbedUri)
        {
            Content = JsonContent.Create(request),
        };
        // Header, not a query parameter — a key in the URL lands in access logs.
        message.Headers.Add("x-goog-api-key", _options.ApiKey);

        using var response = await _http
            .SendAsync(message, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogError(
                "embeddings:failed status={Status} body={Body}",
                (int)response.StatusCode,
                body.Length > 400 ? body[..400] : body);
            throw new HttpRequestException(
                $"Embedding request failed with HTTP {(int)response.StatusCode}.");
        }

        var parsed = await response.Content
            .ReadFromJsonAsync<EmbedResponse>(cancellationToken)
            .ConfigureAwait(false);

        var values = parsed?.Embedding?.Values
            ?? throw new HttpRequestException("Embedding response carried no vector.");

        if (values.Length != Dimensions)
        {
            throw new HttpRequestException(
                $"Embedding model returned {values.Length} dimensions, expected {Dimensions}. "
                + "The Atlas index declares a fixed numDimensions and would reject these vectors.");
        }

        return Normalise(values);
    }

    /// <summary>Mirrors the store's declared width — see ContentChunkVocabulary.Dimensions.</summary>
    private const int Dimensions = 768;

    private static float[] Normalise(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        var norm = Math.Sqrt(sum);
        // A zero vector cannot be normalised; return it untouched rather than
        // producing NaNs that would poison every later comparison.
        if (norm <= double.Epsilon) return v;
        var result = new float[v.Length];
        for (var i = 0; i < v.Length; i++) result[i] = (float)(v[i] / norm);
        return result;
    }

    private sealed record EmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("content")] EmbedContent Content,
        [property: JsonPropertyName("taskType")] string TaskType,
        [property: JsonPropertyName("outputDimensionality")] int OutputDimensionality);

    private sealed record EmbedContent(
        [property: JsonPropertyName("parts")] EmbedPart[] Parts);

    private sealed record EmbedPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed record EmbedResponse(
        [property: JsonPropertyName("embedding")] EmbedVector? Embedding);

    private sealed record EmbedVector(
        [property: JsonPropertyName("values")] float[] Values);
}
