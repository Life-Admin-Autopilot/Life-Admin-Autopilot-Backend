using System.Text.Json.Serialization;
using Life_Admin_Autopilot.BLL.Services;
using Life_Admin_Autopilot.DAL.Kernel.Ops;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Modules;

namespace Life_Admin_Autopilot_Backend.Features.Capabilities;

/// <summary>
/// What this deployment can do for the caller right now.
///
/// <para>
/// <b>Why this exists.</b> Every kill switch is enforced server-side on the route
/// it protects, so the product is correct without this endpoint. But enforcement
/// alone means the app looks completely normal until the user records a voice note
/// and gets an error — they blame their microphone, then the app, then retry. This
/// endpoint lets the app disable the affordance up front and say why, so a pulled
/// switch reads as "paused" rather than "broken".
/// </para>
///
/// <para>
/// <b>true means available.</b> The stored flags are DISABLE switches, so every
/// field here is the inverse of its row. That inversion happens once, on this
/// line, rather than in the client: a boolean called <c>documentScan</c> that
/// means "document scan is off" is exactly the kind of double negative a UI
/// eventually renders backwards.
/// </para>
///
/// <para>
/// <b>The operator's reason is not returned</b> — see
/// <see cref="FeatureDisabled"/>. The client owns the user-facing copy, which is
/// also what lets it be localised; a message assembled on the server would arrive
/// in one language.
/// </para>
/// </summary>
public sealed class CapabilitiesResponse
{
    [JsonPropertyName("aiChat")]
    public bool AiChat { get; init; }

    [JsonPropertyName("documentScan")]
    public bool DocumentScan { get; init; }

    [JsonPropertyName("transcription")]
    public bool Transcription { get; init; }
}

public static class CapabilitiesEndpoints
{
    public static IEndpointRouteBuilder MapCapabilitiesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/me/capabilities", async (
            HttpContext context,
            IFeatureFlagStore flags,
            AsrAvailability asr,
            CancellationToken cancellationToken) =>
        {
            // Authenticated, though nothing here is per-user. Two reasons: an
            // unauthenticated endpoint would tell anyone which of this deployment's
            // capabilities are currently broken, and the app has no reason to ask
            // before it has a session — the surfaces this gates are all behind sign-in.
            context.RequireUser();

            // ListAsync, not three IsDisabledAsync calls: one round trip, and it
            // returns a row for every known flag whether or not anyone has ever
            // flipped it, so a brand-new deployment answers all-available rather
            // than an empty object the client has to interpret.
            var rows = await flags.ListAsync(cancellationToken).ConfigureAwait(false);

            bool Available(string key) =>
                !(rows.FirstOrDefault(r => r.Key == key)?.Disabled ?? false);

            return Results.Json(new CapabilitiesResponse
            {
                AiChat = Available(FeatureFlags.AiChat),
                DocumentScan = Available(FeatureFlags.DocumentScan),

                // Two ways for voice to be off, and the client needs neither of them
                // spelled out — only the answer. The operator's switch is one; the
                // other is the provider itself refusing every call, which is what an
                // exhausted quota looks like from here. Reporting only the switch is
                // what let the app keep offering a microphone into a dead ASR: the
                // flag said "enabled", and it was — the credits were not.
                Transcription = Available(FeatureFlags.Transcription) && asr.IsAvailable,
            });
        })
        .RequireAuthorization();

        return endpoints;
    }
}

/// <summary>Found by the kernel's assembly scanner — no <c>Program.cs</c> edit.</summary>
public sealed class CapabilitiesModule : IEndpointModule
{
    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapCapabilitiesEndpoints();
}
