using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Push;

namespace Life_Admin_Autopilot.Tests.TestDoubles
{
    public class StubFcmAccessTokenProvider : IFcmAccessTokenProvider
    {
        private readonly Result<string> _accessToken;

        private StubFcmAccessTokenProvider(Result<string> accessToken, string? projectId, bool isConfigured)
        {
            _accessToken = accessToken;
            ProjectId = projectId;
            IsConfigured = isConfigured;
        }

        public bool IsConfigured { get; }

        public string? ProjectId { get; }

        public Task<Result<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_accessToken);

        public static StubFcmAccessTokenProvider Valid(string projectId = "life-admin-test") =>
            new(Result<string>.Success("ya29.test-access-token"), projectId, isConfigured: true);

        public static StubFcmAccessTokenProvider Failing(Error error) =>
            new(Result<string>.Failure(error), projectId: null, isConfigured: false);
    }
}
