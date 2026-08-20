using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Push.Models;
using Life_Admin_Autopilot.DAL.Push.Models.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.DAL.Push
{
    public class FcmPushNotificationService : IPushNotificationService
    {
        private static readonly JsonSerializerOptions WireSerializerOptions = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private readonly HttpClient _httpClient;
        private readonly PushNotificationOptions _options;
        private readonly IFcmAccessTokenProvider _accessTokenProvider;
        private readonly ILogger<FcmPushNotificationService> _logger;

        public FcmPushNotificationService(
            HttpClient httpClient,
            IOptions<PushNotificationOptions> options,
            IFcmAccessTokenProvider accessTokenProvider,
            ILogger<FcmPushNotificationService> logger)
        {
            _httpClient = httpClient;
            _options = options.Value;
            _accessTokenProvider = accessTokenProvider;
            _logger = logger;
        }

        /// <inheritdoc />
        /// <remarks>
        /// Deliberately the SAME two checks <see cref="SendAsync"/> makes before it builds
        /// a request, read through one property so the answer given to the client cannot
        /// drift from the answer the send path would give.
        /// </remarks>
        public bool IsConfigured =>
            _accessTokenProvider.IsConfigured && !string.IsNullOrWhiteSpace(ResolvedProjectId);

        // Configuration wins over the credential's own project id, so a deployment can
        // point a key at a different project without reissuing it.
        private string? ResolvedProjectId =>
            _options.ProjectId is { Length: > 0 } ? _options.ProjectId : _accessTokenProvider.ProjectId;

        public async Task<Result<PushNotificationResult>> SendAsync(
            PushNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            var maskedToken = PushTokenMask.Mask(request.DeviceToken);

            if (string.IsNullOrWhiteSpace(request.DeviceToken))
            {
                return Fail(PushErrorCodes.InvalidArgument, "The device token is empty.", maskedToken);
            }

            var projectId = ResolvedProjectId;

            if (string.IsNullOrWhiteSpace(projectId))
            {
                return Fail(
                    PushErrorCodes.NotConfigured,
                    "No Firebase project id is available from configuration or from the service account.",
                    maskedToken);
            }

            var accessTokenResult = await _accessTokenProvider.GetAccessTokenAsync(cancellationToken);
            if (accessTokenResult.IsFailure)
            {
                return Fail(accessTokenResult.Error!.Code, accessTokenResult.Error.Message, maskedToken);
            }

            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Post,
                $"{_options.FcmBaseUrl.TrimEnd('/')}/projects/{projectId}/messages:send")
            {
                Content = JsonContent.Create(BuildWireRequest(request), options: WireSerializerOptions)
            };
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessTokenResult.Value!);

            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Fail(PushErrorCodes.NetworkError, ex.Message, maskedToken);
            }

            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var error = ParseErrorBody(response.StatusCode, rawBody);
                return Fail(error.Code, error.Message, maskedToken);
            }

            return ParseSuccessBody(rawBody, stopwatch.ElapsedMilliseconds, maskedToken);
        }

        private FcmSendWireRequest BuildWireRequest(PushNotificationRequest request) => new()
        {
            Message = new FcmWireMessage
            {
                Token = request.DeviceToken,
                Notification = new FcmWireNotification
                {
                    Title = request.Title,
                    Body = request.Body
                },
                Data = request.Data is { Count: > 0 } ? request.Data : null,
                Android = new FcmWireAndroidConfig
                {
                    Notification = new FcmWireAndroidNotification
                    {
                        ChannelId = _options.AndroidChannelId
                    }
                },
                Apns = new FcmWireApnsConfig
                {
                    Headers = new Dictionary<string, string>
                    {
                        // Reminders are user-visible and time-sensitive, so APNs is told to
                        // deliver immediately rather than batch for power savings.
                        ["apns-priority"] = "10",
                        ["apns-push-type"] = "alert"
                    },
                    Payload = new FcmWireApnsPayload
                    {
                        Aps = new FcmWireAps
                        {
                            Alert = new FcmWireApsAlert
                            {
                                Title = request.Title,
                                Body = request.Body
                            }
                        }
                    }
                }
            }
        };

        // Every failure leaves a log line here, at the point where the FCM detail is still
        // available - callers may summarise, but nothing is dropped silently.
        private Result<PushNotificationResult> Fail(string code, string message, string maskedToken)
        {
            _logger.LogWarning(
                "Push delivery failed for device {DeviceToken}: {ErrorCode} - {ErrorMessage}",
                maskedToken,
                code,
                message);

            return Result<PushNotificationResult>.Failure(new Error(code, message));
        }

        private static Error ParseErrorBody(HttpStatusCode statusCode, string rawBody)
        {
            FcmErrorDetail? detail = null;
            try
            {
                detail = JsonSerializer.Deserialize<FcmErrorResponse>(rawBody)?.Error;
            }
            catch (JsonException)
            {
                // Fall through to the status-code mapping below.
            }

            var fcmErrorCode = detail?.Details?
                .FirstOrDefault(item => item.Type?.EndsWith("google.firebase.fcm.v1.FcmError", StringComparison.Ordinal) == true)?
                .ErrorCode;

            var message = string.IsNullOrWhiteSpace(detail?.Message)
                ? $"HTTP {(int)statusCode}: {rawBody}"
                : $"HTTP {(int)statusCode} ({fcmErrorCode ?? detail!.Status ?? "unknown"}): {detail!.Message}";

            return new Error(MapErrorCode(fcmErrorCode, statusCode), message);
        }

        // https://firebase.google.com/docs/cloud-messaging/send-message#admin_sdk_error_reference
        private static string MapErrorCode(string? fcmErrorCode, HttpStatusCode statusCode) => fcmErrorCode switch
        {
            // The app was uninstalled or the token rotated; SENDER_ID_MISMATCH means the
            // token belongs to a different Firebase project entirely. Both are terminal.
            "UNREGISTERED" => PushErrorCodes.TokenInvalid,
            "SENDER_ID_MISMATCH" => PushErrorCodes.TokenInvalid,
            "INVALID_ARGUMENT" => PushErrorCodes.InvalidArgument,
            "QUOTA_EXCEEDED" => PushErrorCodes.RateLimited,
            "UNAVAILABLE" => PushErrorCodes.Unavailable,
            "INTERNAL" => PushErrorCodes.Unavailable,
            // Firebase could not authenticate with APNs - the iOS auth key or team/bundle
            // ids in the Firebase console are wrong or expired.
            "THIRD_PARTY_AUTH_ERROR" => PushErrorCodes.NotAuthorized,
            _ => statusCode switch
            {
                HttpStatusCode.NotFound => PushErrorCodes.TokenInvalid,
                HttpStatusCode.BadRequest => PushErrorCodes.InvalidArgument,
                HttpStatusCode.Unauthorized => PushErrorCodes.NotAuthorized,
                HttpStatusCode.Forbidden => PushErrorCodes.NotAuthorized,
                HttpStatusCode.TooManyRequests => PushErrorCodes.RateLimited,
                >= HttpStatusCode.InternalServerError => PushErrorCodes.Unavailable,
                _ => PushErrorCodes.GatewayError
            }
        };

        private Result<PushNotificationResult> ParseSuccessBody(string rawBody, long latencyMs, string maskedToken)
        {
            FcmSendWireResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<FcmSendWireResponse>(rawBody);
            }
            catch (JsonException ex)
            {
                return Fail(
                    PushErrorCodes.UnrecognizedResponseShape,
                    $"FCM accepted the message but the response could not be parsed ({ex.Message}). Raw body: {rawBody}",
                    maskedToken);
            }

            if (string.IsNullOrEmpty(parsed?.Name))
            {
                return Fail(
                    PushErrorCodes.UnrecognizedResponseShape,
                    $"FCM accepted the message but returned no message name. Raw body: {rawBody}",
                    maskedToken);
            }

            _logger.LogInformation(
                "Push accepted by FCM for device {DeviceToken} as {MessageId} in {LatencyMs}ms",
                maskedToken,
                parsed.Name,
                latencyMs);

            return Result<PushNotificationResult>.Success(new PushNotificationResult
            {
                MessageId = parsed.Name,
                LatencyMs = latencyMs
            });
        }
    }
}
