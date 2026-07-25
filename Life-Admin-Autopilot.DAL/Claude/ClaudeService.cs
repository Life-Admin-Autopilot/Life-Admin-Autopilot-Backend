using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Claude.Models;
using Life_Admin_Autopilot.DAL.Claude.Models.Internal;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Claude
{
    public class ClaudeService : IClaudeService
    {
        private static readonly JsonSerializerOptions WireSerializerOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly ClaudeOptions _options;

        public ClaudeService(HttpClient httpClient, IOptions<ClaudeOptions> options)
        {
            _httpClient = httpClient;
            _options = options.Value;
        }

        public async Task<Result<ClaudeCompletionResult>> GetCompletionAsync(
            ClaudeCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            var wireRequest = new ClaudeChatWireRequest
            {
                ModelId = _options.ModelId,
                Messages = request.Messages.ToList(),
                SystemPrompt = request.SystemPrompt,
                MaxTokens = request.MaxTokens ?? _options.DefaultMaxTokens
            };

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _options.ChatEndpointUrl)
            {
                Content = JsonContent.Create(wireRequest, options: WireSerializerOptions)
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Result<ClaudeCompletionResult>.Failure(new Error("CLAUDE_NETWORK_ERROR", ex.Message));
            }

            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return Result<ClaudeCompletionResult>.Failure(ParseErrorBody(response.StatusCode, rawBody));
            }

            var completionText = TryExtractCompletionText(rawBody);
            if (completionText is null)
            {
                return Result<ClaudeCompletionResult>.Failure(new Error(
                    "CLAUDE_UNRECOGNIZED_RESPONSE_SHAPE",
                    $"Gateway returned a successful ({(int)response.StatusCode}) response, but no known completion field was found. Raw body: {rawBody}"));
            }

            return Result<ClaudeCompletionResult>.Success(new ClaudeCompletionResult
            {
                CompletionText = completionText,
                ModelId = _options.ModelId,
                RawResponseBody = rawBody
            });
        }

        private static Error ParseErrorBody(System.Net.HttpStatusCode statusCode, string rawBody)
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ClaudeErrorResponse>(rawBody);
                if (parsed?.Error is { } detail && !string.IsNullOrWhiteSpace(detail.Message))
                {
                    return new Error(detail.Code ?? "CLAUDE_GATEWAY_ERROR", detail.Message);
                }
            }
            catch (JsonException)
            {
                // Fall through to the generic error below.
            }

            return new Error("CLAUDE_GATEWAY_ERROR", $"HTTP {(int)statusCode}: {rawBody}");
        }

        // The gateway's success response shape has never been observed (every real test
        // call so far hit a model-approval/policy error, not a 2xx - see
        // Claude_Code_Brief_Stories_1_2). This defensively probes the most common chat-API
        // response conventions rather than assuming one. Once a real successful response
        // is seen, replace this with a precise typed DTO matching the confirmed shape.
        private static string? TryExtractCompletionText(string rawBody)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(rawBody);
            }
            catch (JsonException)
            {
                return null;
            }

            using (document)
            {
                var root = document.RootElement;

                if (TryGetString(root, "completion", out var completion)) return completion;
                if (TryGetString(root, "response", out var responseText)) return responseText;
                if (TryGetString(root, "output_text", out var outputText)) return outputText;
                if (TryGetString(root, "result", out var result)) return result;

                if (root.TryGetProperty("content", out var content))
                {
                    if (content.ValueKind == JsonValueKind.String) return content.GetString();
                    if (content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
                        && TryGetString(content[0], "text", out var blockText)) return blockText;
                }

                if (root.TryGetProperty("message", out var message) && TryGetString(message, "content", out var messageContent))
                    return messageContent;

                if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("message", out var choiceMessage)
                        && TryGetString(choiceMessage, "content", out var choiceMessageContent))
                        return choiceMessageContent;

                    if (TryGetString(firstChoice, "text", out var choiceText)) return choiceText;
                }

                return null;
            }
        }

        private static bool TryGetString(JsonElement element, string propertyName, out string? value)
        {
            if (element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(propertyName, out var property)
                && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return value is not null;
            }

            value = null;
            return false;
        }
    }
}