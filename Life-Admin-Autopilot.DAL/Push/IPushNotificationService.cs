using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Push.Models;

namespace Life_Admin_Autopilot.DAL.Push
{
    // The only thing in the codebase allowed to talk to FCM directly. Feature code sends
    // through the BLL notification service, which owns token lookup and cleanup.
    public interface IPushNotificationService
    {
        // Whether this environment can reach FCM AT ALL: a service account loaded and a
        // project id resolved. It answers the setup question, not the per-send one - a
        // configured sender still fails on an expired token or an offline handset.
        //
        // It exists because the CLIENT has to know. The app switches its own local
        // notification schedule off when the server takes over delivery, and it used to
        // do that on the strength of the register call succeeding - which only ever
        // proved a row was stored. On an environment with no credential that turned a
        // working local fallback into silence. See DevicesController.Register.
        bool IsConfigured { get; }

        Task<Result<PushNotificationResult>> SendAsync(
            PushNotificationRequest request,
            CancellationToken cancellationToken = default);
    }
}
