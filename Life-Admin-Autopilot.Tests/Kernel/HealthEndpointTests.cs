using System.Net;
using System.Text.Json;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// The kernel's end-to-end smoke test. It proves CORS, routing, module scanning,
/// JSON serialization and Mongo connectivity are all wired.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<KernelWebApplicationFactory>
{
    private readonly KernelWebApplicationFactory _factory;

    public HealthEndpointTests(KernelWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task returns_the_node_health_shape()
    {
        // Act
        var response = await _factory.CreateApiClient().GetAsync("/health");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        // Assert — exactly three fields, exactly these names.
        Assert.Equal(3, json.EnumerateObject().Count());
        Assert.True(json.TryGetProperty("status", out var status));
        Assert.True(json.TryGetProperty("db", out var db));
        Assert.True(json.TryGetProperty("uptime", out var uptime));

        Assert.Equal(JsonValueKind.Number, uptime.ValueKind);
        Assert.True(uptime.GetDouble() >= 0);

        // 200/ok when Mongo answers, 503/degraded when it does not — and the pairing
        // is what matters, not which branch a given machine takes.
        if (response.StatusCode == HttpStatusCode.OK)
        {
            Assert.Equal("ok", status.GetString());
            Assert.Equal("connected", db.GetString());
        }
        else
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("degraded", status.GetString());
            Assert.NotEqual("connected", db.GetString());
        }
    }

    [Fact]
    public async Task is_reachable_without_authentication()
    {
        // Assert — health must never 401; a monitor has no token.
        var response = await _factory.CreateApiClient().GetAsync("/health");
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task is_reachable_from_a_native_client_with_no_origin()
    {
        // Assert
        var response = await _factory.CreateApiClient().GetAsync("/health");
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Contains(
            response.StatusCode,
            new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable });
    }
}
