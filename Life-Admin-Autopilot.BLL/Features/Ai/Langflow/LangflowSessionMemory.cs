namespace Life_Admin_Autopilot.BLL.Features.Ai.Langflow;

/// <summary>
/// <see cref="IAgentSessionMemory"/> against Langflow's monitor API: one DELETE that
/// drops every message stored under a retired session id.
///
/// <para>
/// <b>This is hygiene, not correctness.</b> The reset has already minted a new
/// generation, so the retired session is never addressed again and nothing the agent
/// still holds can reach a future answer. What the delete buys is that a user who
/// pressed "clear" does not leave a verbatim transcript sitting in a second system —
/// and that a long-lived instance does not accumulate one dead session per reset per
/// user forever.
/// </para>
///
/// <para>
/// <b>It is deliberately impatient.</b> The caller is a user-facing request that has
/// already done its real work, so a slow or wedged Langflow must cost the reset a few
/// seconds at most. The run timeout is measured in minutes and is the wrong ceiling
/// here.
/// </para>
/// </summary>
public sealed class LangflowSessionMemory : IAgentSessionMemory
{
    /// <summary>
    /// How long the reset will wait for the remote deletion. Short on purpose: see
    /// the class remarks. Not configurable, because there is no deployment in which
    /// a user should wait longer than this for a cleanup they did not ask for.
    /// </summary>
    public static readonly TimeSpan ForgetTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _clients;
    private readonly LangflowOptions _options;

    public LangflowSessionMemory(IHttpClientFactory clients, LangflowOptions options)
    {
        _clients = clients;
        _options = options;
    }

    public async Task ForgetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        using var client = _clients.CreateClient(LangflowOptions.HttpClientName);
        client.Timeout = ForgetTimeout;

        using var request = new HttpRequestMessage(HttpMethod.Delete, _options.SessionMessagesUri(sessionId));

        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // Raised so the CALLER can log it. A 404 for a session Langflow never stored
        // is the normal case for a user who reset before ever asking anything, and
        // EnsureSuccessStatusCode treats it like any other failure — which is right:
        // the caller's policy is "log it and carry on", so there is nothing to gain
        // from classifying failures here and something to lose if a real 500 were
        // quietly filtered out as unremarkable.
        response.EnsureSuccessStatusCode();
    }
}
