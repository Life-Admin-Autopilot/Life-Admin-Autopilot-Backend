using System.Net;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Push;
using Life_Admin_Autopilot.DAL.Push.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.Tests.Push
{
    public class FcmPushNotificationServiceTests
    {
        // Long enough to be masked in logs, like a real FCM registration token.
        private static readonly string DeviceToken = "e8Xq7T" + new string('x', 140) + "9Zk4";

        [Fact]
        public async Task SendAsync_ReturnsMessageId_WhenFcmAcceptsTheMessage()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.OK,
                """{"name":"projects/life-admin-test/messages/0:1500415314455276"}""");
            var (service, _) = CreateService(handler);

            var result = await service.SendAsync(Request());

            Assert.True(result.IsSuccess);
            Assert.Equal("projects/life-admin-test/messages/0:1500415314455276", result.Value!.MessageId);
            Assert.Equal(
                "https://fcm.example/v1/projects/life-admin-test/messages:send",
                handler.LastRequestUri!.ToString());
            Assert.Equal("Bearer ya29.test-access-token", handler.LastAuthorizationHeader);
        }

        // One send has to cover both platforms: FCM applies the android block for Android
        // tokens and hands the apns block to APNs for iOS ones.
        [Fact]
        public async Task SendAsync_SendsBothAndroidAndApnsConfiguration()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"name":"projects/p/messages/1"}""");
            var (service, _) = CreateService(handler);

            await service.SendAsync(Request());

            var body = handler.LastRequestBody!;
            Assert.Contains("\"android\"", body);
            Assert.Contains("\"channel_id\":\"reminders\"", body);
            Assert.Contains("\"apns\"", body);
            Assert.Contains("\"apns-priority\":\"10\"", body);
            Assert.Contains("\"aps\"", body);
        }

        [Fact]
        public async Task SendAsync_ReportsTokenInvalid_WhenFcmSaysUnregistered()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, UnregisteredErrorBody);
            var (service, _) = CreateService(handler);

            var result = await service.SendAsync(Request());

            Assert.True(result.IsFailure);
            Assert.Equal(PushErrorCodes.TokenInvalid, result.Error!.Code);
            Assert.True(PushErrorCodes.IsTokenPermanentlyInvalid(result.Error.Code));
        }

        // Acceptance criterion: a failed delivery is logged, not silently dropped.
        [Fact]
        public async Task SendAsync_LogsAWarning_WhenDeliveryFails()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, UnregisteredErrorBody);
            var (service, logger) = CreateService(handler);

            await service.SendAsync(Request());

            var warning = Assert.Single(logger.Warnings);
            Assert.Contains(PushErrorCodes.TokenInvalid, warning.Message);
            Assert.Contains("UNREGISTERED", warning.Message);
        }

        [Fact]
        public async Task SendAsync_DoesNotLogTheFullDeviceToken()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, UnregisteredErrorBody);
            var (service, logger) = CreateService(handler);

            await service.SendAsync(Request());

            Assert.All(logger.Entries, entry => Assert.DoesNotContain(DeviceToken, entry.Message));
            Assert.Contains(logger.Entries, entry => entry.Message.Contains("e8Xq7T...9Zk4"));
        }

        // A rejected request must not be reported as a dead device: acting on it would
        // retire every user's token the first time we shipped a malformed payload.
        [Fact]
        public async Task SendAsync_DoesNotTreatInvalidArgumentAsADeadToken()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.BadRequest,
                """
                {"error":{"code":400,"message":"Invalid value at 'message.notification.title'","status":"INVALID_ARGUMENT",
                "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"INVALID_ARGUMENT"}]}}
                """);
            var (service, logger) = CreateService(handler);

            var result = await service.SendAsync(Request());

            Assert.Equal(PushErrorCodes.InvalidArgument, result.Error!.Code);
            Assert.False(PushErrorCodes.IsTokenPermanentlyInvalid(result.Error.Code));
            Assert.Single(logger.Warnings);
        }

        // Firebase could not authenticate with APNs - an iOS-side setup problem that must
        // surface as a credentials error, not as a bad device token.
        [Fact]
        public async Task SendAsync_ReportsNotAuthorized_WhenApnsCredentialsAreRejected()
        {
            var handler = new StubHttpMessageHandler(
                HttpStatusCode.Forbidden,
                """
                {"error":{"code":403,"message":"Auth error from APNS or Web Push Service","status":"PERMISSION_DENIED",
                "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"THIRD_PARTY_AUTH_ERROR"}]}}
                """);
            var (service, _) = CreateService(handler);

            var result = await service.SendAsync(Request());

            Assert.Equal(PushErrorCodes.NotAuthorized, result.Error!.Code);
        }

        [Fact]
        public async Task SendAsync_ReportsNetworkError_WhenFcmCannotBeReached()
        {
            var handler = StubHttpMessageHandler.Throwing(new HttpRequestException("no route to host"));
            var (service, logger) = CreateService(handler);

            var result = await service.SendAsync(Request());

            Assert.Equal(PushErrorCodes.NetworkError, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        [Fact]
        public async Task SendAsync_ReportsUnrecognizedShape_WhenFcmAcceptsWithoutAMessageName()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{}");
            var (service, logger) = CreateService(handler);

            var result = await service.SendAsync(Request());

            Assert.Equal(PushErrorCodes.UnrecognizedResponseShape, result.Error!.Code);
            Assert.Single(logger.Warnings);
        }

        [Fact]
        public async Task SendAsync_FailsWithoutCallingFcm_WhenNoServiceAccountIsConfigured()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"name":"projects/p/messages/1"}""");
            var service = new FcmPushNotificationService(
                new HttpClient(handler),
                Options.Create(new PushNotificationOptions { FcmBaseUrl = "https://fcm.example/v1" }),
                StubFcmAccessTokenProvider.Failing(new Error(PushErrorCodes.NotConfigured, "no credentials")),
                new RecordingLogger<FcmPushNotificationService>());

            var result = await service.SendAsync(Request());

            Assert.Equal(PushErrorCodes.NotConfigured, result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
        }

        [Fact]
        public async Task SendAsync_RejectsAnEmptyDeviceToken()
        {
            var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"name":"projects/p/messages/1"}""");
            var (service, logger) = CreateService(handler);

            var result = await service.SendAsync(new PushNotificationRequest { Title = "t", Body = "b" });

            Assert.Equal(PushErrorCodes.InvalidArgument, result.Error!.Code);
            Assert.Equal(0, handler.CallCount);
            Assert.Single(logger.Warnings);
        }

        private const string UnregisteredErrorBody =
            """
            {"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND",
            "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}
            """;

        private static PushNotificationRequest Request() => new()
        {
            DeviceToken = DeviceToken,
            Title = "Renew your passport",
            Body = "Due in 3 days",
            Data = new Dictionary<string, string> { ["taskId"] = "abc123" }
        };

        private static (FcmPushNotificationService Service, RecordingLogger<FcmPushNotificationService> Logger) CreateService(
            HttpMessageHandler handler)
        {
            var logger = new RecordingLogger<FcmPushNotificationService>();
            var service = new FcmPushNotificationService(
                new HttpClient(handler),
                Options.Create(new PushNotificationOptions
                {
                    FcmBaseUrl = "https://fcm.example/v1",
                    AndroidChannelId = "reminders"
                }),
                StubFcmAccessTokenProvider.Valid(),
                logger);

            return (service, logger);
        }
    }
}
