using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Life_Admin_Autopilot.DAL.Entities
{
    // One row per physical device, keyed by the FCM registration token the Capacitor
    // client hands us. FCM rotates tokens, so the same device can produce several rows
    // over time - stale ones are deactivated when FCM reports them as unregistered.
    public class DeviceToken
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string? Id { get; set; }

        [BsonElement("UserId")]
        public string UserId { get; set; } = string.Empty;

        [BsonElement("Token")]
        public string Token { get; set; } = string.Empty;

        [BsonElement("Platform")]
        [BsonRepresentation(BsonType.String)]
        public DevicePlatform Platform { get; set; }

        [BsonElement("DeviceModel")]
        public string? DeviceModel { get; set; }

        [BsonElement("RegisteredAt")]
        public DateTime RegisteredAt { get; set; }

        [BsonElement("LastSeenAt")]
        public DateTime LastSeenAt { get; set; }

        [BsonElement("IsActive")]
        public bool IsActive { get; set; } = true;

        [BsonElement("DeactivatedAt")]
        public DateTime? DeactivatedAt { get; set; }

        // Why the token stopped being usable (e.g. the FCM error code that retired it),
        // so a dead device is diagnosable rather than just missing.
        [BsonElement("DeactivationReason")]
        public string? DeactivationReason { get; set; }
    }
}