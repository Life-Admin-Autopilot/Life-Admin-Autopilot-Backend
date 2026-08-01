using Life_Admin_Autopilot.DAL.Common;

namespace Life_Admin_Autopilot.DAL.Push
{
    public interface IFcmAccessTokenProvider
    {
        // False when no service account was supplied - pushes cannot work in this
        // environment and the send path fails fast instead of calling FCM.
        bool IsConfigured { get; }

        // Taken from the service account credential, so a deployment only has to supply
        // one secret rather than a secret plus a matching project id.
        string? ProjectId { get; }

        Task<Result<string>> GetAccessTokenAsync(CancellationToken cancellationToken = default);
    }
}
