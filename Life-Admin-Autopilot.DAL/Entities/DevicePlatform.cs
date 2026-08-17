using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot.DAL.Entities
{
    /// <summary>
    /// Which store issued the push token.
    ///
    /// <para>
    /// <b>Serialised by NAME, and that is load-bearing.</b> The Capacitor client
    /// posts <c>"platform": "Ios"</c>, and without this converter System.Text.Json
    /// accepts only the numeric form — so every real registration threw and became
    /// a 500. Nothing surfaced it: the client fires the call on cold start and
    /// ignores the response, so no device was ever reachable by push and the only
    /// symptom was notifications that silently never arrived.
    /// </para>
    ///
    /// <para>
    /// The attribute is on the ENUM rather than on the global MVC options, so it
    /// changes how this one type travels and nothing else. Mongo already stores it
    /// as a string (<c>BsonRepresentation(BsonType.String)</c> on
    /// <see cref="DeviceToken.Platform"/>) — JSON was the only place it was a
    /// number.
    /// </para>
    ///
    /// <para>
    /// Integer values are still accepted on the wire by the converter's default;
    /// the controller rejects anything not <c>Enum.IsDefined</c>, which is what
    /// turns <c>"platform": 42</c> into a 400 instead of a stored nonsense value.
    /// </para>
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DevicePlatform
    {
        Android,
        Ios
    }
}
