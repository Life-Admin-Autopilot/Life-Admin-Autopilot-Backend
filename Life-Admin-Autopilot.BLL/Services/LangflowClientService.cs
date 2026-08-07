using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.BLL.Settings;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class LangflowClientService : ILangflowClientService
    {
        private readonly HttpClient _httpClient;
        private readonly LangflowSettings _settings;

        public LangflowClientService(
            HttpClient httpClient,IOptions<LangflowSettings> options)
        {
            _httpClient = httpClient;
            _settings = options.Value;

            _httpClient.BaseAddress = new Uri(_settings.Url);
            _httpClient.DefaultRequestHeaders.Add(
            "x-api-key",
            _settings.ApiKey);
        }
        public async Task<PlanningResponse> RunAsync(LangflowRequest request)
        {
            var payload = new
            {
                input_value = "",
                input_type = "chat",
                output_type = "chat",
                tweaks = new Dictionary<string, object>
                {
                    ["Prompt Template"] = new
                    {
                        currentDate = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                        accessToken = request.AccessToken,
                        mode = request.Mode,
                        transcript = request.Transcript,
                        pendingTasks = request.PendingTasks,
                        answers = request.Answers
                    }
                }
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var response = await _httpClient.PostAsync(
                $"/api/v1/run/{_settings.FlowId}?stream=false",
                content);

            //response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Langflow returned {(int)response.StatusCode} " +
                    $"({response.StatusCode}). Response: {jsonResponse}");
            }

            using var document = JsonDocument.Parse(jsonResponse);

            var rawText = document.RootElement
                .GetProperty("outputs")[0]
                .GetProperty("outputs")[0]
                .GetProperty("outputs")
                .GetProperty("message")
                .GetProperty("message")
                .GetString();

            if (string.IsNullOrWhiteSpace(rawText))
            {
                throw new InvalidOperationException(
                    "Langflow returned an empty response.");
            }

            var cleanJson = rawText
                .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
                .Replace("```", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            var result = JsonSerializer.Deserialize<PlanningResponse>(
                cleanJson,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (result is null)
            {
                throw new InvalidOperationException(
                    "Failed to deserialize Langflow response.");
            }

            return result;
        }
    }
}
