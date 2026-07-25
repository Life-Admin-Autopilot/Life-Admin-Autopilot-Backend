using System.Diagnostics;
using System.Globalization;
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

            var stopwatch = Stopwatch.StartNew();
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
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return Result<ClaudeCompletionResult>.Failure(ParseErrorBody(response.StatusCode, rawBody));
            }

            return ParseSuccessBody(rawBody, stopwatch.ElapsedMilliseconds);
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

        // Shape confirmed empirically against the real gateway - see
        // ClaudeChatWireResponse for the specific test that established this.
        private static Result<ClaudeCompletionResult> ParseSuccessBody(string rawBody, long latencyMs)
        {
            ClaudeChatWireResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ClaudeChatWireResponse>(rawBody);
            }
            catch (JsonException ex)
            {
                return Result<ClaudeCompletionResult>.Failure(new Error(
                    "CLAUDE_UNRECOGNIZED_RESPONSE_SHAPE",
                    $"Gateway returned a successful response that could not be parsed ({ex.Message}). Raw body: {rawBody}"));
            }

            if (string.IsNullOrEmpty(parsed?.OutputText))
            {
                return Result<ClaudeCompletionResult>.Failure(new Error(
                    "CLAUDE_UNRECOGNIZED_RESPONSE_SHAPE",
                    $"Gateway returned a successful response with no output_text field. Raw body: {rawBody}"));
            }

            return Result<ClaudeCompletionResult>.Success(new ClaudeCompletionResult
            {
                CompletionText = parsed.OutputText,
                ModelId = parsed.ModelId ?? string.Empty,
                FallbackUsed = parsed.Usage?.FallbackUsed ?? false,
                InputTokens = parsed.Usage?.InputTokens ?? 0,
                OutputTokens = parsed.Usage?.OutputTokens ?? 0,
                TotalTokens = parsed.Usage?.TotalTokens ?? 0,
                StopReason = parsed.Usage?.StopReason,
                EstimatedCostUsd = ParseCost(parsed.EstimatedCostUsd),
                ActualCostUsd = ParseCost(parsed.ActualCostUsd),
                LatencyMs = latencyMs,
                RawResponseBody = rawBody
            });
        }

        private static decimal? ParseCost(string? value) =>
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }
}