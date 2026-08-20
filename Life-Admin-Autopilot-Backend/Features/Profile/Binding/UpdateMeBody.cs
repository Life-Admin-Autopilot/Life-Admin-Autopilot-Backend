using System.Text.Json;
using System.Text.Json.Serialization;

namespace Life_Admin_Autopilot_Backend.Features.Profile.Binding;

/// <summary>
/// <c>PATCH /me</c> body. Every field optional; the Node schema is a PLAIN
/// <c>z.object</c>, so unknown top-level keys are STRIPPED, not rejected — read it
/// with <c>KernelBodyOptions.Lenient</c>. Verified live: a body carrying
/// <c>email</c>, <c>subscription</c> and <c>hasPassword</c> returns 200 and
/// changes none of them.
/// </summary>
/// <remarks>
/// <c>JsonElement</c> throughout, never <c>JsonElement?</c>. An absent key and an
/// explicit <c>null</c> are different outcomes on the reference — <c>{}</c> is a
/// 200 no-op while <c>{"displayName":null}</c> is a 400 "Expected string, received
/// null" — and a nullable CLR property collapses the two.
/// <c>default(JsonElement)</c> has <see cref="JsonValueKind.Undefined"/>, which is
/// exactly the distinction needed.
/// </remarks>
public sealed class UpdateMeBody
{
    [JsonPropertyName("displayName")]
    public JsonElement DisplayName { get; init; }

    [JsonPropertyName("preferredDomains")]
    public JsonElement PreferredDomains { get; init; }

    [JsonPropertyName("hasOnboarded")]
    public JsonElement HasOnboarded { get; init; }

    [JsonPropertyName("onboardingAnswers")]
    public JsonElement OnboardingAnswers { get; init; }

    [JsonPropertyName("timezone")]
    public JsonElement Timezone { get; init; }

    [JsonPropertyName("timezoneFollowsDevice")]
    public JsonElement TimezoneFollowsDevice { get; init; }

    [JsonPropertyName("locale")]
    public JsonElement Locale { get; init; }

    [JsonPropertyName("localeFollowsDevice")]
    public JsonElement LocaleFollowsDevice { get; init; }

    [JsonPropertyName("theme")]
    public JsonElement Theme { get; init; }

    [JsonPropertyName("textSize")]
    public JsonElement TextSize { get; init; }

    [JsonPropertyName("mic")]
    public JsonElement Mic { get; init; }

    [JsonPropertyName("notifications")]
    public JsonElement Notifications { get; init; }

    [JsonPropertyName("privacy")]
    public JsonElement Privacy { get; init; }

    [JsonPropertyName("imports")]
    public JsonElement Imports { get; init; }
}

/// <summary>
/// <c>DELETE /me</c> body — a JSON body on a DELETE, which several stacks discard
/// by default. A missing body is treated as <c>{}</c>, and the schema is lenient,
/// so <c>{"bogus":1}</c> behaves identically to <c>{}</c>. Verified live.
/// </summary>
public sealed class DeleteMeBody
{
    [JsonPropertyName("password")]
    public JsonElement Password { get; init; }
}
