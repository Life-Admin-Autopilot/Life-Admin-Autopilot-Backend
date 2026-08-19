using System.Net;

namespace Life_Admin_Autopilot.Tests.TestDoubles
{
    public class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHttpMessageHandler(HttpStatusCode statusCode, string body)
            : this(_ => new HttpResponseMessage(statusCode) { Content = new StringContent(body) })
        {
        }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public int CallCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        public string? LastRequestBody { get; private set; }

        public string? LastAuthorizationHeader { get; private set; }

        /// <summary>
        /// Every request header, so a provider that authenticates with something other than
        /// <c>Authorization</c> can be asserted on — Azure Speech uses
        /// <c>Ocp-Apim-Subscription-Key</c>.
        /// </summary>
        public Dictionary<string, string> LastRequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The Content-Type actually sent, boundary parameter and all.</summary>
        public string? LastContentType { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            LastAuthorizationHeader = request.Headers.Authorization?.ToString();

            LastRequestHeaders.Clear();
            foreach (var header in request.Headers)
            {
                LastRequestHeaders[header.Key] = string.Join(", ", header.Value);
            }

            LastContentType = request.Content?.Headers.ContentType?.ToString();
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _responder(request);
        }

        public static StubHttpMessageHandler Throwing(Exception exception) =>
            new(_ => throw exception);
    }
}
