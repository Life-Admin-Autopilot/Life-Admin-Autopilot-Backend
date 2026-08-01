using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Push.Models.Internal
{
    // Google's standard error envelope. The precise reason lives in details[], under the
    // entry whose @type is google.firebase.fcm.v1.FcmError - the top-level status is only
    // the coarse HTTP mapping (NOT_FOUND, INVALID_ARGUMENT, ...).
    internal class FcmErrorResponse
    {
        [JsonPropertyName("error")]
        public FcmErrorDetail? Error { get; set; }
    }

    internal class FcmErrorDetail
    {
        [JsonPropertyName("code")]
        public int Code { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("details")]
        public List<FcmErrorDetailItem>? Details { get; set; }
    }

    internal class FcmErrorDetailItem
    {
        [JsonPropertyName("@type")]
        public string? Type { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }
    }
}
