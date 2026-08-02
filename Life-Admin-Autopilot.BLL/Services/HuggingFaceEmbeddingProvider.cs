using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.BLL.Settings;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class HuggingFaceEmbeddingProvider : IEmbeddingProvider
    {
        private readonly HttpClient _httpClient;
        private readonly HuggingFaceSettings _settings;

        public HuggingFaceEmbeddingProvider(HttpClient httpClient, IOptions<HuggingFaceSettings> options)
        {
            _httpClient = httpClient;
            _settings = options.Value;

            _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        }
        public async Task<float[]> GenerateEmbeddingAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<float>();

            var payload = new
            {
                inputs = text
            };

            using var response = await _httpClient.PostAsJsonAsync(
                _settings.EmbeddingModelUrl,
                payload);

            response.EnsureSuccessStatusCode();

            var embedding =
                await response.Content.ReadFromJsonAsync<float[]>();

            return embedding ?? Array.Empty<float>();
        }
    }
}
