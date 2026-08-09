using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Kernel.Dtos;
using Life_Admin_Autopilot.DAL.Kernel.Documents;

namespace Life_Admin_Autopilot.BLL.Features.Account;

/// <summary>
/// Wire shape of <c>GET /me/subscription</c> — <c>{ subscription: { … } }</c>.
/// </summary>
public sealed class SubscriptionResponse
{
    [JsonPropertyName("subscription")]
    public SubscriptionStateDto Subscription { get; init; } = new();
}

/// <summary>
/// Wire shape of <c>GET /me/billing/invoices</c>. The list is hard-coded empty —
/// there is no payment provider — but the envelope is the contract the frontend's
/// empty state renders from, so it ships as a real object rather than a bare array.
/// </summary>
public sealed class InvoicesResponse
{
    [JsonPropertyName("invoices")]
    public IReadOnlyList<object> Invoices { get; init; } = Array.Empty<object>();
}

/// <summary>
/// Wire shape of <c>GET /me/integrations</c>. Hard-coded empty until OAuth handlers
/// exist. Unrelated to the live Google integration routes, which are a different
/// router in Node and a different slice here.
/// </summary>
public sealed class IntegrationsResponse
{
    [JsonPropertyName("integrations")]
    public IReadOnlyList<object> Integrations { get; init; } = Array.Empty<object>();
}

/// <summary>
/// The Account slice's mappers. <c>SubscriptionStateDto</c> itself is a kernel type
/// (it is embedded in <c>UserDto</c> too); what lives here is the standalone
/// document-to-DTO transform the subscription endpoint needs.
/// </summary>
public static class AccountMappers
{
    /// <summary>
    /// <para>
    /// The sub-schema is declared <c>_id: false</c>, so unlike a top-level document
    /// there is no <c>_id</c> to rename or drop — the transform is a straight field
    /// copy. What matters is the DTO's
    /// <c>[JsonIgnore(WhenWritingNull)]</c> on <c>renewsAt</c>/<c>canceledAt</c>:
    /// Mongoose never stores an unset optional, so those keys are ABSENT on the wire,
    /// not <c>null</c>. Emitting <c>"renewsAt": null</c> is a silent parity break —
    /// the status matches and only the body differs.
    /// </para>
    /// </summary>
    public static SubscriptionStateDto ToDto(this SubscriptionStateDocument document) => new()
    {
        Tier = document.Tier,
        RenewsAt = document.RenewsAt,
        CanceledAt = document.CanceledAt,
    };
}
