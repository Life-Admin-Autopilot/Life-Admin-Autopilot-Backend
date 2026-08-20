using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Services;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Push.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;

namespace Life_Admin_Autopilot.Tests.Push
{
    public class NotificationServiceTests
    {
        private const string UserId = "6a1f0c74-0f0e-4f5a-9a52-2f1b0e6f4a11";
        private static readonly string AndroidToken = "andr01" + new string('a', 140) + "aa01";
        private static readonly string IosToken = "ios001" + new string('i', 140) + "ii02";

        [Fact]
        public async Task SendToUserAsync_SendsToEveryActiveDevice()
        {
            var repository = SeededRepository();
            var push = StubPushNotificationService.AlwaysSucceeds();
            var service = CreateService(repository, push, out _);

            var report = await service.SendToUserAsync(UserId, new PushMessage("Renew passport", "Due in 3 days"));

            Assert.Equal(2, report.SentCount);
            Assert.Equal(0, report.FailedCount);
            Assert.Equal(
                new[] { AndroidToken, IosToken },
                push.Requests.Select(request => request.DeviceToken).ToArray());
        }

        [Fact]
        public async Task SendToUserAsync_PassesTitleBodyAndDataThrough()
        {
            var repository = SeededRepository();
            var push = StubPushNotificationService.AlwaysSucceeds();
            var service = CreateService(repository, push, out _);

            await service.SendToUserAsync(
                UserId,
                new PushMessage("Renew passport", "Due in 3 days", new Dictionary<string, string> { ["taskId"] = "t1" }));

            var sent = push.Requests[0];
            Assert.Equal("Renew passport", sent.Title);
            Assert.Equal("Due in 3 days", sent.Body);
            Assert.Equal("t1", sent.Data!["taskId"]);
        }

        [Fact]
        public async Task SendToUserAsync_DeactivatesTheDevice_WhenFcmReportsAnInvalidToken()
        {
            var repository = SeededRepository();
            var push = StubPushNotificationService.AlwaysFails(PushErrorCodes.TokenInvalid, "Requested entity was not found.");
            var service = CreateService(repository, push, out _);

            var report = await service.SendToUserAsync(UserId, new PushMessage("Renew passport", "Due in 3 days"));

            Assert.Equal(0, report.SentCount);
            Assert.Equal(2, report.FailedCount);
            Assert.All(report.Results, result => Assert.True(result.TokenDeactivated));
            Assert.All(repository.All, device => Assert.False(device.IsActive));
            Assert.All(repository.All, device => Assert.Equal(PushErrorCodes.TokenInvalid, device.DeactivationReason));
        }

        // Acceptance criterion: a failed delivery is logged rather than silently dropped.
        [Fact]
        public async Task SendToUserAsync_LogsAWarningPerFailedDevice()
        {
            var repository = SeededRepository();
            var push = StubPushNotificationService.AlwaysFails(PushErrorCodes.TokenInvalid, "Requested entity was not found.");
            var service = CreateService(repository, push, out var logger);

            await service.SendToUserAsync(UserId, new PushMessage("Renew passport", "Due in 3 days"));

            Assert.Equal(2, logger.Warnings.Count());
            Assert.All(logger.Warnings, warning => Assert.Contains(PushErrorCodes.TokenInvalid, warning.Message));
            Assert.All(logger.Warnings, warning => Assert.Contains("Renew passport", warning.Message));
        }

        // A temporary FCM outage must not cost the user their device registration.
        [Fact]
        public async Task SendToUserAsync_KeepsTheDevice_WhenTheFailureIsTransient()
        {
            var repository = SeededRepository();
            var push = StubPushNotificationService.AlwaysFails(PushErrorCodes.Unavailable, "FCM is unavailable.");
            var service = CreateService(repository, push, out var logger);

            var report = await service.SendToUserAsync(UserId, new PushMessage("Renew passport", "Due in 3 days"));

            Assert.All(report.Results, result => Assert.False(result.TokenDeactivated));
            Assert.All(repository.All, device => Assert.True(device.IsActive));
            Assert.Equal(2, logger.Warnings.Count());
        }

        [Fact]
        public async Task SendToUserAsync_ReportsPerDevice_WhenOnlyOneDeviceFails()
        {
            var repository = SeededRepository();
            var push = new StubPushNotificationService(request =>
                request.DeviceToken == IosToken
                    ? DAL.Common.Result<PushNotificationResult>.Failure(
                        new DAL.Common.Error(PushErrorCodes.TokenInvalid, "gone"))
                    : DAL.Common.Result<PushNotificationResult>.Success(new PushNotificationResult
                    {
                        MessageId = "projects/p/messages/1"
                    }));
            var service = CreateService(repository, push, out _);

            var report = await service.SendToUserAsync(UserId, new PushMessage("Renew passport", "Due in 3 days"));

            Assert.Equal(1, report.SentCount);
            Assert.Equal(1, report.FailedCount);
            Assert.True(repository.All.Single(device => device.Token == AndroidToken).IsActive);
            Assert.False(repository.All.Single(device => device.Token == IosToken).IsActive);
        }

        // A reminder that reaches nobody is a distinct, diagnosable state - not a success.
        [Fact]
        public async Task SendToUserAsync_LogsAWarning_WhenTheUserHasNoRegisteredDevice()
        {
            var repository = new InMemoryDeviceTokenRepository();
            var push = StubPushNotificationService.AlwaysSucceeds();
            var service = CreateService(repository, push, out var logger);

            var report = await service.SendToUserAsync(UserId, new PushMessage("Renew passport", "Due in 3 days"));

            Assert.False(report.HasRegisteredDevices);
            Assert.Empty(push.Requests);
            Assert.Single(logger.Warnings);
        }

        [Fact]
        public async Task SendToUserAsync_SkipsDeactivatedDevices()
        {
            var repository = SeededRepository();
            await repository.DeactivateAsync(IosToken, PushErrorCodes.TokenInvalid);
            var push = StubPushNotificationService.AlwaysSucceeds();
            var service = CreateService(repository, push, out _);

            await service.SendToUserAsync(UserId, new PushMessage("Renew passport", "Due in 3 days"));

            Assert.Equal(new[] { AndroidToken }, push.Requests.Select(request => request.DeviceToken).ToArray());
        }

        [Fact]
        public async Task RegisterDeviceAsync_ReactivatesATokenThatWasPreviouslyRetired()
        {
            var repository = SeededRepository();
            await repository.DeactivateAsync(AndroidToken, PushErrorCodes.TokenInvalid);
            var service = CreateService(repository, StubPushNotificationService.AlwaysSucceeds(), out _);

            await service.RegisterDeviceAsync(
                UserId,
                new RegisterDeviceRequest(AndroidToken, DevicePlatform.Android, "Pixel 8"));

            var device = repository.All.Single(candidate => candidate.Token == AndroidToken);
            Assert.True(device.IsActive);
            Assert.Null(device.DeactivationReason);
            Assert.Equal("Pixel 8", device.DeviceModel);
        }

        [Fact]
        public async Task RegisterDeviceAsync_DoesNotEchoTheRawTokenBack()
        {
            var repository = new InMemoryDeviceTokenRepository();
            var service = CreateService(repository, StubPushNotificationService.AlwaysSucceeds(), out _);

            var response = await service.RegisterDeviceAsync(
                UserId,
                new RegisterDeviceRequest(AndroidToken, DevicePlatform.Android));

            Assert.NotEqual(AndroidToken, response.Device.DeviceToken);
            Assert.Equal(PushTokenMask.Mask(AndroidToken), response.Device.DeviceToken);
        }

        // The client switches its own local reminder schedule off when the server takes
        // over delivery. These two pin the signal it makes that decision on: registering
        // successfully is NOT the same question as whether this server can send, and
        // conflating them blacked out every Android device on a deployment with no
        // credential. See DeviceRegistrationResponse.
        [Fact]
        public async Task RegisterDeviceAsync_SaysTheServerDelivers_WhenPushIsConfigured()
        {
            var repository = new InMemoryDeviceTokenRepository();
            var push = StubPushNotificationService.AlwaysSucceeds();
            var service = CreateService(repository, push, out _);

            var response = await service.RegisterDeviceAsync(
                UserId,
                new RegisterDeviceRequest(AndroidToken, DevicePlatform.Android));

            Assert.True(response.ServerDelivers);
        }

        [Fact]
        public async Task RegisterDeviceAsync_SaysTheServerDoesNot_WhenNoCredentialIsConfigured()
        {
            var repository = new InMemoryDeviceTokenRepository();
            var push = new StubPushNotificationService(
                _ => Result<PushNotificationResult>.Failure(
                    new Error(PushErrorCodes.NotConfigured, "no service account")))
            {
                IsConfigured = false
            };
            var service = CreateService(repository, push, out _);

            var response = await service.RegisterDeviceAsync(
                UserId,
                new RegisterDeviceRequest(AndroidToken, DevicePlatform.Android));

            // The device is still stored - it becomes reachable the moment a credential
            // is supplied, with no re-registration needed.
            Assert.Single(repository.All);
            Assert.False(response.ServerDelivers);
        }

        [Fact]
        public async Task UnregisterDeviceAsync_OnlyRemovesTheCallersOwnDevice()
        {
            var repository = SeededRepository();
            var service = CreateService(repository, StubPushNotificationService.AlwaysSucceeds(), out _);

            var removedForOtherUser = await service.UnregisterDeviceAsync("someone-else", AndroidToken);
            var removedForOwner = await service.UnregisterDeviceAsync(UserId, AndroidToken);

            Assert.False(removedForOtherUser);
            Assert.True(removedForOwner);
            Assert.DoesNotContain(repository.All, device => device.Token == AndroidToken);
        }

        private static InMemoryDeviceTokenRepository SeededRepository()
        {
            var repository = new InMemoryDeviceTokenRepository();
            repository.Seed(
                new DeviceToken
                {
                    Id = "1",
                    UserId = UserId,
                    Token = AndroidToken,
                    Platform = DevicePlatform.Android,
                    IsActive = true
                },
                new DeviceToken
                {
                    Id = "2",
                    UserId = UserId,
                    Token = IosToken,
                    Platform = DevicePlatform.Ios,
                    IsActive = true
                });

            return repository;
        }

        private static NotificationService CreateService(
            InMemoryDeviceTokenRepository repository,
            StubPushNotificationService push,
            out RecordingLogger<NotificationService> logger)
        {
            logger = new RecordingLogger<NotificationService>();

            return new NotificationService(repository, push, logger);
        }
    }
}
