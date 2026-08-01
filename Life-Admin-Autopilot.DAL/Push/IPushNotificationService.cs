using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Push.Models;

namespace Life_Admin_Autopilot.DAL.Push
{
    // The only thing in the codebase allowed to talk to FCM directly. Feature code sends
    // through the BLL notification service, which owns token lookup and cleanup.
    public interface IPushNotificationService
    {
        Task<Result<PushNotificationResult>> SendAsync(
            PushNotificationRequest request,
            CancellationToken cancellationToken = default);
    }
}
