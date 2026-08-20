using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Push;
using Life_Admin_Autopilot.DAL.Push.Models;

namespace Life_Admin_Autopilot.Tests.TestDoubles
{
    public class StubPushNotificationService : IPushNotificationService
    {
        private readonly Func<PushNotificationRequest, Result<PushNotificationResult>> _responder;

        public StubPushNotificationService(Func<PushNotificationRequest, Result<PushNotificationResult>> responder)
        {
            _responder = responder;
        }

        public List<PushNotificationRequest> Requests { get; } = new();

        // A stub exists to stand in for a WORKING sender, so it reports configured unless a
        // test is specifically exercising the unconfigured path.
        public bool IsConfigured { get; init; } = true;

        public Task<Result<PushNotificationResult>> SendAsync(
            PushNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            return Task.FromResult(_responder(request));
        }

        public static StubPushNotificationService AlwaysSucceeds() =>
            new(_ => Result<PushNotificationResult>.Success(new PushNotificationResult
            {
                MessageId = "projects/life-admin-test/messages/1"
            }));

        public static StubPushNotificationService AlwaysFails(string errorCode, string message = "failed") =>
            new(_ => Result<PushNotificationResult>.Failure(new Error(errorCode, message)));
    }
}
