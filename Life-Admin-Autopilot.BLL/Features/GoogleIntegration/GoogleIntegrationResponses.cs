using System.Text.Json.Serialization;
using Life_Admin_Autopilot.DAL.Features.GoogleIntegration;

namespace Life_Admin_Autopilot.BLL.Features.GoogleIntegration;

/// <summary>
/// An <c>Integration</c> after <c>toJSON</c>.
///
/// <para>
/// <b>The deletions are the whole point.</b> <c>refreshTokenEnc</c>,
/// <c>accessTokenEnc</c> and <c>accessTokenExpiresAt</c> are stripped, plus the
/// usual <c>_id</c> → <c>id</c> and <c>__v</c>. No token material is ever
/// serialised, and there is no code path from the document to the wire that does not
/// go through this type.
/// </para>
///
/// <para>
/// Property order follows the Mongoose schema with <c>id</c> last, per KERNEL.md §6
/// (the transform assigns <c>ret.id</c> after deleting <c>_id</c>, so it lands at the
/// end of the object). <b>UNVERIFIED</b> against a live connected account — there
/// are no Google credentials on the reference machine, so no body of this shape has
/// ever been observed.
/// </para>
/// </summary>
public sealed class GoogleIntegrationDto
{
    [JsonPropertyName("userId")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; init; } = IntegrationVocabulary.Google;

    [JsonPropertyName("externalAccountId")]
    public string ExternalAccountId { get; init; } = string.Empty;

    [JsonPropertyName("externalAccountEmail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalAccountEmail { get; init; }

    [JsonPropertyName("grantedScopes")]
    public IReadOnlyList<string> GrantedScopes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("status")]
    public string Status { get; init; } = IntegrationVocabulary.StatusActive;

    [JsonPropertyName("lastError")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastError { get; init; }

    [JsonPropertyName("calendarSyncToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CalendarSyncToken { get; init; }

    [JsonPropertyName("calendarSyncedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CalendarSyncedAt { get; init; }

    [JsonPropertyName("tasksSyncedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? TasksSyncedAt { get; init; }

    [JsonPropertyName("importDomain")]
    public string ImportDomain { get; init; } = IntegrationVocabulary.DefaultImportDomain;

    [JsonPropertyName("connectedAt")]
    public DateTime ConnectedAt { get; init; }

    [JsonPropertyName("revokedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? RevokedAt { get; init; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; init; }

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
}

/// <summary>
/// <c>GET /integrations/google</c>. Both keys are ALWAYS present:
/// <c>available</c> is a real boolean and <c>integration</c> is an explicit
/// <c>null</c> when nothing is connected — so this one nullable property must NOT
/// carry <c>[JsonIgnore(WhenWritingNull)]</c>. Verified live as
/// <c>{"available":false,"integration":null}</c>.
/// </summary>
public sealed class GoogleStatusResponse
{
    [JsonPropertyName("available")]
    public bool Available { get; init; }

    // JsonIgnoreCondition.Never overrides the serializer-wide
    // DefaultIgnoreCondition = WhenWritingNull, which is the right default
    // everywhere else (Mongoose omits unset optionals) and exactly wrong here: the
    // live response is {"available":false,"integration":null}, and dropping the key
    // is the silent kind of parity break where the status matches and only the body
    // differs.
    [JsonPropertyName("integration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public GoogleIntegrationDto? Integration { get; init; }
}

/// <summary><c>POST /integrations/google/authorize</c>.</summary>
public sealed class GoogleAuthorizeResponse
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

/// <summary><c>DELETE /integrations/google</c>.</summary>
public sealed class GoogleDisconnectResponse
{
    [JsonPropertyName("removed")]
    public bool Removed { get; init; } = true;
}

/// <summary><c>POST /integrations/google/sync</c>. UNVERIFIED — see the contract.</summary>
public sealed class GoogleSyncResponse
{
    [JsonPropertyName("integration")]
    public GoogleIntegrationDto Integration { get; init; } = new();

    [JsonPropertyName("calendar")]
    public GoogleCalendarSyncResult Calendar { get; init; } = new();

    [JsonPropertyName("tasks")]
    public GoogleTasksSyncResult Tasks { get; init; } = new();
}

/// <summary>The slice's only document-to-wire transform.</summary>
public static class GoogleIntegrationMappers
{
    public static GoogleIntegrationDto ToDto(this IntegrationDocument document) => new()
    {
        UserId = document.UserId.ToString(),
        Provider = document.Provider,
        ExternalAccountId = document.ExternalAccountId,
        ExternalAccountEmail = document.ExternalAccountEmail,
        GrantedScopes = document.GrantedScopes,
        Status = document.Status,
        LastError = document.LastError,
        CalendarSyncToken = document.CalendarSyncToken,
        CalendarSyncedAt = document.CalendarSyncedAt,
        TasksSyncedAt = document.TasksSyncedAt,
        ImportDomain = document.ImportDomain,
        ConnectedAt = document.ConnectedAt,
        RevokedAt = document.RevokedAt,
        CreatedAt = document.CreatedAt,
        UpdatedAt = document.UpdatedAt,
        Id = document.Id.ToString(),
    };
}
