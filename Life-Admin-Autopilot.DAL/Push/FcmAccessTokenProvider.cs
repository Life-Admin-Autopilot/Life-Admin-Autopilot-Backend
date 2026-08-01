using Google.Apis.Auth.OAuth2;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Push.Models;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Push
{
    // Mints the OAuth2 access token FCM HTTP v1 expects. GoogleCredential caches the
    // token and refreshes it shortly before expiry, so this is registered as a singleton
    // and every send reuses the same in-memory token.
    public class FcmAccessTokenProvider : IFcmAccessTokenProvider
    {
        private const string MessagingScope = "https://www.googleapis.com/auth/firebase.messaging";

        private readonly GoogleCredential? _credential;
        private readonly string? _initializationError;

        public FcmAccessTokenProvider(IOptions<PushNotificationOptions> options)
        {
            var settings = options.Value;
            ServiceAccountCredential? serviceAccount = null;

            try
            {
                serviceAccount = LoadServiceAccount(settings);
                _credential = serviceAccount?.ToGoogleCredential().CreateScoped(MessagingScope);
            }
            catch (Exception ex)
            {
                // Bad credentials must not take the whole API down at startup - every
                // other feature still works, pushes just report why they cannot run.
                _initializationError = ex.Message;
            }

            ProjectId = string.IsNullOrWhiteSpace(settings.ProjectId)
                ? serviceAccount?.ProjectId
                : settings.ProjectId;
        }

        public bool IsConfigured => _credential is not null;

        public string? ProjectId { get; }

        public async Task<Result<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            if (_credential is null)
            {
                return Result<string>.Failure(new Error(
                    PushErrorCodes.NotConfigured,
                    _initializationError
                        ?? "No FCM service account is configured. Set FCM_SERVICE_ACCOUNT_JSON or FCM_SERVICE_ACCOUNT_FILE."));
            }

            try
            {
                var accessToken = await _credential.UnderlyingCredential
                    .GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);

                return string.IsNullOrEmpty(accessToken)
                    ? Result<string>.Failure(new Error(
                        PushErrorCodes.NotAuthorized,
                        "Google returned an empty access token for the FCM service account."))
                    : Result<string>.Success(accessToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Result<string>.Failure(new Error(
                    PushErrorCodes.NotAuthorized,
                    $"Could not obtain an FCM access token: {ex.Message}"));
            }
        }

        // Loaded as a ServiceAccountCredential rather than a bare GoogleCredential so the
        // Firebase project id travels with the key and never has to be configured twice.
        private static ServiceAccountCredential? LoadServiceAccount(PushNotificationOptions settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.ServiceAccountJson))
            {
                return CredentialFactory.FromJson<ServiceAccountCredential>(settings.ServiceAccountJson);
            }

            if (!string.IsNullOrWhiteSpace(settings.ServiceAccountFilePath))
            {
                return CredentialFactory.FromFile<ServiceAccountCredential>(settings.ServiceAccountFilePath);
            }

            return null;
        }
    }
}
