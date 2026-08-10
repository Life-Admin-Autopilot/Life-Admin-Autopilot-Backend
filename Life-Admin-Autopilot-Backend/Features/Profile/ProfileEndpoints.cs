using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Auth;
using Life_Admin_Autopilot.BLL.Features.Profile;
using Life_Admin_Autopilot.BLL.Kernel.Mappers;
using Life_Admin_Autopilot.BLL.Kernel.UserData;
using Life_Admin_Autopilot.DAL.Features.Account;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot.DAL.Features.Profile;
using Life_Admin_Autopilot.DAL.Kernel.Errors;
using Life_Admin_Autopilot.DAL.Kernel.UserData;
using Life_Admin_Autopilot_Backend.Features.Profile.Binding;
using Life_Admin_Autopilot_Backend.Kernel.Auth;
using Life_Admin_Autopilot_Backend.Kernel.Binding;

namespace Life_Admin_Autopilot_Backend.Features.Profile;

/// <summary>
/// Ports <c>routes/me.ts</c> and <c>routes/me.export.ts</c>: <c>PATCH /me</c>,
/// <c>DELETE /me</c>, <c>GET /me/export</c>. All three are authenticated and none
/// is rate limited.
/// </summary>
public static class ProfileEndpoints
{
    /// <summary>
    /// Every branch of all three routes answers this when the row behind a still-valid
    /// access token is gone. The token stays cryptographically valid until it expires,
    /// so "account deleted" is a routine 404 rather than an auth failure.
    /// </summary>
    private static AppException UserNotFound() =>
        AppException.NotFound("user_not_found", "Account no longer exists.");

    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPatch("/me", UpdateMeAsync).RequireAuthorization();
        endpoints.MapDelete("/me", DeleteMeAsync).RequireAuthorization();
        endpoints.MapGet("/me/export", ExportMeAsync).RequireAuthorization();

        return endpoints;
    }

    /// <summary>
    /// <c>PATCH /me</c> — partial settings update.
    ///
    /// <para>
    /// The body is LENIENT: the Node schema is a plain <c>z.object</c>, so
    /// <c>email</c>, <c>subscription</c> or <c>hasPassword</c> in the body are
    /// silently stripped rather than rejected. Those fields are not client-writable,
    /// and 200-with-no-effect is the measured behaviour.
    /// </para>
    /// </summary>
    private static async Task<IResult> UpdateMeAsync(
        HttpContext context,
        IProfileRepository profiles,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var body = await KernelBody
            .ReadAsync<UpdateMeBody>(
                context,
                KernelBodyOptions.Lenient(UpdateMeValidator.Message, UpdateMeValidator.Code),
                cancellationToken)
            .ConfigureAwait(false);

        var set = UpdateMeValidator.BuildSet(body);

        var user = await profiles
            .ApplyPatchAsync(caller.Id, set, clock.GetUtcNow().UtcDateTime, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UserNotFound();

        return Results.Ok(new AuthUserResponse { User = user.ToDto() });
    }

    /// <summary>
    /// <c>DELETE /me</c> — the account and every document that references it.
    ///
    /// <para>
    /// <b>This route carries a JSON body</b>, which several stacks discard on a
    /// DELETE. The confirmation password travels in it, so it must be read.
    /// </para>
    ///
    /// <para>
    /// The cascade itself is <see cref="UserDataErasureService"/>, i.e. the
    /// registered <see cref="IUserDataEraser"/> set — NOT a hardcoded collection
    /// list. Node keeps one hand-maintained list of twelve
    /// <c>deleteMany</c> calls inside this handler; reproducing it here would make
    /// this file the merge point for every slice in the port and would silently omit
    /// any collection whose slice landed after it was written. See
    /// <c>docs/DIVERGENCES.md</c>.
    /// </para>
    /// </summary>
    private static async Task<IResult> DeleteMeAsync(
        HttpContext context,
        IAccountProfileRepository profiles,
        IAuthCredentialStore credentials,
        UserDataErasureService erasure,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var body = await KernelBody
            .ReadAsync<DeleteMeBody>(
                context,
                KernelBodyOptions.Lenient("That delete request didn't look right."),
                cancellationToken)
            .ConfigureAwait(false);

        var password = ReadPassword(body);

        var user = await profiles.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false)
            ?? throw UserNotFound();

        // A magic-link-only account has no credential, so it deletes with no
        // confirmation at all — demanding one would ask for something the user
        // cannot give. `hasPassword` is exactly this test, and the client reads it
        // to decide whether to prompt.
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            if (password is null)
            {
                throw AppException.BadRequest(
                    "password_required",
                    "Enter your password to delete your account.");
            }

            var verified = await credentials
                .VerifyPasswordAsync(user.IdentityUserId, password, cancellationToken)
                .ConfigureAwait(false);

            if (!verified)
            {
                throw AuthErrors.PasswordIncorrect();
            }
        }

        await erasure
            .EraseAsync(new UserErasureContext(user.Id, user.IdentityUserId), cancellationToken)
            .ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// <c>z.string().min(1).optional()</c>. An absent key is the "no password
    /// supplied" branch (400 <c>password_required</c> if the account has one), while
    /// an empty string is a SHAPE failure and reports <c>invalid_body</c> instead.
    /// Measured — the two are different responses.
    /// </summary>
    private static string? ReadPassword(DeleteMeBody body)
    {
        if (body.Password.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        var issues = new List<ValidationIssue>();

        if (body.Password.ValueKind != JsonValueKind.String)
        {
            issues.Add(ValidationIssue.At(
                "password",
                ZodMessages.ExpectedType("string", ZodTypeName(body.Password))));
        }
        else if ((body.Password.GetString() ?? string.Empty).Length < 1)
        {
            issues.Add(ValidationIssue.At("password", ZodMessages.TooShort(1)));
        }

        if (issues.Count > 0)
        {
            throw AppException.BadRequest(
                "invalid_body",
                "That delete request didn't look right.",
                ValidationDetails.AsFlattened(issues));
        }

        return body.Password.GetString();
    }

    /// <summary>
    /// <c>GET /me/export</c> — a FILE DOWNLOAD, not an ordinary JSON response.
    ///
    /// <para>
    /// The handler sets both headers by hand and sends a pretty-printed STRING
    /// through <c>res.send</c>, so the two-space indentation is part of the bytes a
    /// client receives. <see cref="Results.Text(string, string, System.Text.Encoding)"/>
    /// is the equivalent — <c>Results.Json</c> would re-serialize and compact it.
    /// </para>
    /// </summary>
    private static async Task<IResult> ExportMeAsync(
        HttpContext context,
        IAccountProfileRepository profiles,
        IAccountExportService export,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var caller = context.RequireUser();

        var user = await profiles.FindByIdAsync(caller.Id, cancellationToken).ConfigureAwait(false)
            ?? throw UserNotFound();

        var now = clock.GetUtcNow().UtcDateTime;
        var json = await export.BuildAsync(user, now, cancellationToken).ConfigureAwait(false);

        // `new Date().toISOString().slice(0, 10)` — UTC, never the server's local day.
        var stamp = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"kitto-export-{stamp}.json\"";

        return Results.Text(json, "application/json; charset=utf-8");
    }

    private static string ZodTypeName(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Null => "null",
        _ => "undefined",
    };
}
