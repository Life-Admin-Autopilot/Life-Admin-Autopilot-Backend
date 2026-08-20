using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Push;
using Life_Admin_Autopilot.DAL.Push.Models;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class NotificationService : INotificationService
    {
        private readonly IDeviceTokenRepository _deviceTokenRepository;
        private readonly IPushNotificationService _pushNotificationService;
        private readonly ILogger<NotificationService> _logger;

        public NotificationService(
            IDeviceTokenRepository deviceTokenRepository,
            IPushNotificationService pushNotificationService,
            ILogger<NotificationService> logger)
        {
            _deviceTokenRepository = deviceTokenRepository;
            _pushNotificationService = pushNotificationService;
            _logger = logger;
        }

        /// <inheritdoc />
        public bool PushDeliveryConfigured => _pushNotificationService.IsConfigured;

        public async Task<DeviceRegistrationResponse> RegisterDeviceAsync(string userId, RegisterDeviceRequest request)
        {
            var stored = await _deviceTokenRepository.UpsertAsync(new DeviceToken
            {
                UserId = userId,
                Token = request.Token!,
                Platform = request.Platform!.Value,
                DeviceModel = request.DeviceModel
            });

            _logger.LogInformation(
                "Registered {Platform} device {DeviceToken} for user {UserId}",
                stored.Platform,
                PushTokenMask.Mask(stored.Token),
                userId);

            // Logged at registration rather than only at send time: this is the moment a
            // device is told to stand down, so it is the moment worth being able to find
            // in a log when someone reports getting no reminders.
            if (!PushDeliveryConfigured)
            {
                _logger.LogWarning(
                    "Device {DeviceToken} registered for user {UserId}, but push is NOT configured on this server - "
                    + "the client will keep delivering reminders locally. Set FCM_SERVICE_ACCOUNT_JSON or FCM_SERVICE_ACCOUNT_FILE.",
                    PushTokenMask.Mask(stored.Token),
                    userId);
            }

            return new DeviceRegistrationResponse(ToResponse(stored), PushDeliveryConfigured);
        }

        public Task<bool> UnregisterDeviceAsync(string userId, string token) =>
            _deviceTokenRepository.DeleteAsync(userId, token);

        public async Task<IReadOnlyList<RegisteredDeviceResponse>> GetDevicesAsync(string userId)
        {
            var devices = await _deviceTokenRepository.GetActiveByUserIdAsync(userId);

            return devices.Select(ToResponse).ToList();
        }

        public async Task<PushDeliveryReport> SendToUserAsync(
            string userId,
            PushMessage message,
            CancellationToken cancellationToken = default)
        {
            var devices = await _deviceTokenRepository.GetActiveByUserIdAsync(userId);

            if (devices.Count == 0)
            {
                // Not an error - the user simply has not granted push permission yet -
                // but it is logged, otherwise a reminder that reaches nobody looks like
                // a successful send from the caller's side.
                _logger.LogWarning(
                    "No active device is registered for user {UserId}; notification '{Title}' was not delivered",
                    userId,
                    message.Title);

                return PushDeliveryReport.From(Array.Empty<PushDeliveryResult>());
            }

            var results = new List<PushDeliveryResult>(devices.Count);
            foreach (var device in devices)
            {
                results.Add(await SendToDeviceAsync(device.Token, userId, message, cancellationToken));
            }

            return PushDeliveryReport.From(results);
        }

        public Task<PushDeliveryResult> SendToTokenAsync(
            string deviceToken,
            PushMessage message,
            CancellationToken cancellationToken = default) =>
            SendToDeviceAsync(deviceToken, userId: null, message, cancellationToken);

        private async Task<PushDeliveryResult> SendToDeviceAsync(
            string deviceToken,
            string? userId,
            PushMessage message,
            CancellationToken cancellationToken)
        {
            var maskedToken = PushTokenMask.Mask(deviceToken);

            var result = await _pushNotificationService.SendAsync(
                new PushNotificationRequest
                {
                    DeviceToken = deviceToken,
                    Title = message.Title,
                    Body = message.Body,
                    Data = message.Data
                },
                cancellationToken);

            if (result.IsSuccess)
            {
                return new PushDeliveryResult
                {
                    DeviceToken = maskedToken,
                    Succeeded = true,
                    MessageId = result.Value!.MessageId
                };
            }

            var error = result.Error!;
            var deactivated = false;

            // FCM has told us this device can never receive again, so the token is retired
            // instead of being retried on every future reminder.
            if (PushErrorCodes.IsTokenPermanentlyInvalid(error.Code))
            {
                deactivated = await _deviceTokenRepository.DeactivateAsync(deviceToken, error.Code);
            }

            _logger.LogWarning(
                "Notification '{Title}' was not delivered to device {DeviceToken} for user {UserId}: {ErrorCode} - {ErrorMessage}. Token deactivated: {TokenDeactivated}",
                message.Title,
                maskedToken,
                userId ?? "(none)",
                error.Code,
                error.Message,
                deactivated);

            return new PushDeliveryResult
            {
                DeviceToken = maskedToken,
                Succeeded = false,
                ErrorCode = error.Code,
                ErrorMessage = error.Message,
                TokenDeactivated = deactivated
            };
        }

        private static RegisteredDeviceResponse ToResponse(DeviceToken device) => new(
            PushTokenMask.Mask(device.Token),
            device.Platform,
            device.DeviceModel,
            device.RegisteredAt,
            device.LastSeenAt);
    }
}
