using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Embeddings
{
    public class HuggingFaceEmbeddingService : IEmbeddingService
    {
        private readonly HttpClient _httpClient;
        private readonly EmbeddingOptions _options;
        private readonly ILogger<HuggingFaceEmbeddingService> _logger;

        public HuggingFaceEmbeddingService(
            HttpClient httpClient,
            IOptions<EmbeddingOptions> options,
            ILogger<HuggingFaceEmbeddingService> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _logger = logger;
        }

        public string ModelId => _options.ModelId;

        public async Task<Result<float[]>> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                return Fail(EmbeddingErrorCodes.NotConfigured,
                    "No embedding API key is configured for this environment.");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return Fail(EmbeddingErrorCodes.EmptyText, "There is nothing to embed.");
            }

            var url = $"/hf-inference/models/{_options.ModelId}/pipeline/feature-extraction";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    // The array form is used even for one string: it is the shape that
                    // returns a predictable [[...]] rather than varying by model.
                    Content = JsonContent.Create(new { inputs = new[] { text } })
                };
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);

                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    _logger.LogWarning(
                        "Embedding request failed with {StatusCode}: {Body}",
                        response.StatusCode,
                        Truncate(body));

                    return Fail(MapStatusCode(response.StatusCode),
                        $"The embedding provider returned {(int)response.StatusCode}.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

                var vector = Flatten(document.RootElement);
                if (vector is null)
                {
                    return Fail(EmbeddingErrorCodes.BadResponse,
                        "The embedding provider returned a shape that could not be read.");
                }

                if (vector.Length != _options.Dimensions)
                {
                    // Caught here rather than at the database: a vector of the wrong
                    // length is either the wrong model or a changed one, and both mean
                    // search would silently stop working.
                    _logger.LogWarning(
                        "{Model} returned {Actual} dimensions, expected {Expected}",
                        _options.ModelId, vector.Length, _options.Dimensions);

                    return Fail(EmbeddingErrorCodes.WrongDimensions,
                        $"The model returned {vector.Length} dimensions but the index needs {_options.Dimensions}.");
                }

                return Result<float[]>.Success(vector);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller gave up - that is not a provider failure, so let it surface.
                throw;
            }
            catch (TaskCanceledException)
            {
                return Fail(EmbeddingErrorCodes.Timeout, "The embedding request timed out.");
            }
            catch (HttpRequestException exception)
            {
                _logger.LogWarning(exception, "Could not reach the embedding provider");

                return Fail(EmbeddingErrorCodes.NetworkError, "Could not reach the embedding provider.");
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Could not parse the embedding response");

                return Fail(EmbeddingErrorCodes.BadResponse, "The embedding response could not be parsed.");
            }
        }

        // Feature-extraction nests its output differently depending on the model's
        // pooling - [[...]] for one, [[[...]]] for another. Descend to the first array of
        // numbers rather than assuming a depth.
        private static float[]? Flatten(JsonElement element)
        {
            while (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
            {
                var first = element[0];
                if (first.ValueKind == JsonValueKind.Number)
                {
                    var values = new float[element.GetArrayLength()];
                    var index = 0;
                    foreach (var item in element.EnumerateArray())
                    {
                        values[index++] = item.GetSingle();
                    }

                    return values;
                }

                element = first;
            }

            return null;
        }

        private static string MapStatusCode(HttpStatusCode statusCode) => statusCode switch
        {
            HttpStatusCode.TooManyRequests => EmbeddingErrorCodes.RateLimited,
            HttpStatusCode.PaymentRequired => EmbeddingErrorCodes.QuotaExceeded,
            HttpStatusCode.Unauthorized => EmbeddingErrorCodes.NotConfigured,
            HttpStatusCode.Forbidden => EmbeddingErrorCodes.NotConfigured,
            _ => EmbeddingErrorCodes.BadResponse
        };

        private static string Truncate(string value) =>
            value.Length <= 300 ? value : value[..300];

        private static Result<float[]> Fail(string code, string message) =>
            Result<float[]>.Failure(new Error(code, message));
    }
}
