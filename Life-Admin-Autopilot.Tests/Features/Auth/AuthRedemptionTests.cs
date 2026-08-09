using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Features.Auth;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Auth;

/// <summary>
/// The five token/code REDEMPTION SUCCESS paths, plus the invariants that only
/// become observable once a secret is actually redeemed.
///
/// <para>
/// <b>Why these need their own file and their own trick.</b> Neither the parity
/// harness nor an ordinary end-to-end test can reach these branches: the secret
/// leaves the server only by email, and the database stores just its sha256. So
/// every test here mints the token row directly, using the SAME hash the
/// production code would compute, and then redeems it through the public HTTP
/// API. That keeps the assertion on real routing, real validation and real
/// persistence while sidestepping the mailbox.
/// </para>
///
/// <para>
/// The preimages are not interchangeable and getting them backwards is a silent
/// security failure rather than a crash, which is exactly why
/// <see cref="binds_a_code_to_the_account_it_was_minted_for"/> exists:
/// a mailed LINK hashes the raw secret alone, while a 6-digit CODE hashes
/// <c>"&lt;userId&gt;:&lt;purpose&gt;:&lt;code&gt;"</c>.
/// </para>
/// </summary>
public sealed class AuthRedemptionTests : IClassFixture<AuthWebApplicationFactory>
{
    private const string Password = "password123";
    private const string Code = "424242";

    private readonly AuthWebApplicationFactory _factory;

    public AuthRedemptionTests(AuthWebApplicationFactory factory) => _factory = factory;

    // ---- Mailed link tokens ------------------------------------------------

    [Fact]
    public async Task verify_email_redeems_a_mailed_link_token_and_stamps_emailVerifiedAt()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var raw = await MintLinkTokenAsync(account.UserId, VerificationPurposes.EmailVerification);

        var response = await PostAsync("/auth/verify-email", new { token = raw });
        var json = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Absent before, present after — and it is a timestamp, not a bool.
        Assert.True(json.GetProperty("user").TryGetProperty("emailVerifiedAt", out var verifiedAt));
        Assert.NotNull(verifiedAt.GetString());
    }

    [Fact]
    public async Task a_link_token_is_single_use()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var raw = await MintLinkTokenAsync(account.UserId, VerificationPurposes.EmailVerification);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync("/auth/verify-email", new { token = raw })).StatusCode);

        // A replay is indistinguishable from an unknown token.
        var replay = await PostAsync("/auth/verify-email", new { token = raw });

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        await AssertErrorAsync(replay, "invalid_verification_token", "This verification link is invalid or has expired.");
    }

    [Fact]
    public async Task an_expired_link_token_is_rejected()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var raw = await MintLinkTokenAsync(
            account.UserId,
            VerificationPurposes.EmailVerification,
            expiresAt: DateTime.UtcNow.AddMinutes(-1));

        var response = await PostAsync("/auth/verify-email", new { token = raw });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "invalid_verification_token", "This verification link is invalid or has expired.");
    }

    /// <summary>
    /// A token is bound to ONE purpose. A reset token presented to verify-email is
    /// rejected — otherwise any mailed secret would unlock any flow.
    /// </summary>
    [Fact]
    public async Task a_link_token_minted_for_another_purpose_is_rejected()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var raw = await MintLinkTokenAsync(account.UserId, VerificationPurposes.PasswordReset);

        var response = await PostAsync("/auth/verify-email", new { token = raw });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "invalid_verification_token", "This verification link is invalid or has expired.");
    }

    // ---- Password reset ----------------------------------------------------

    [Fact]
    public async Task reset_password_swaps_the_credential_and_revokes_every_session()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var raw = await MintLinkTokenAsync(account.UserId, VerificationPurposes.PasswordReset);

        var reset = await PostAsync("/auth/reset-password", new { token = raw, newPassword = "brandnewpassword" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        // The credential really moved in the Identity store.
        var withOld = await PostAsync("/auth/signin", new { email = account.Email, password = Password });
        var withNew = await PostAsync("/auth/signin", new { email = account.Email, password = "brandnewpassword" });

        Assert.Equal(HttpStatusCode.Unauthorized, withOld.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withNew.StatusCode);

        // No keep-this-device escape hatch on this route: the caller's own refresh
        // token is revoked too, unlike change-password.
        var refresh = await PostAsync("/auth/refresh", new { refreshToken = account.RefreshToken });

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        await AssertErrorAsync(refresh, "invalid_refresh_token", "This session has expired. Please sign in again.");
    }

    /// <summary>
    /// The token is consumed BEFORE the user is loaded, so a deleted account yields
    /// the same code with a different message — and the token is already spent.
    /// </summary>
    [Fact]
    public async Task reset_password_on_a_deleted_account_reports_Account_not_found()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var raw = await MintLinkTokenAsync(account.UserId, VerificationPurposes.PasswordReset);
        await DeleteUserAsync(account.UserId);

        var response = await PostAsync("/auth/reset-password", new { token = raw, newPassword = "brandnewpassword" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "invalid_reset_token", "Account not found.");
    }

    // ---- Magic link --------------------------------------------------------

    [Fact]
    public async Task magic_consume_redeems_a_link_and_returns_user_and_tokens()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var raw = await MintLinkTokenAsync(account.UserId, VerificationPurposes.MagicLink);

        var response = await PostAsync("/auth/magic-consume", new { token = raw });
        var json = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(account.Email, json.GetProperty("user").GetProperty("email").GetString());
        Assert.Equal(43, json.GetProperty("tokens").GetProperty("refreshToken").GetString()!.Length);

        // Redeeming the link proves mailbox control, so it verifies the address.
        Assert.True(json.GetProperty("user").TryGetProperty("emailVerifiedAt", out _));
    }

    /// <summary>
    /// The deliberate inconsistency: this route answers <b>404 user_not_found</b>
    /// where /auth/verify-email and /auth/reset-password answer 400 for the very
    /// same "token was good, account is gone" situation. Ported as-is.
    /// </summary>
    [Fact]
    public async Task magic_consume_on_a_deleted_account_is_404_where_the_others_are_400()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var raw = await MintLinkTokenAsync(account.UserId, VerificationPurposes.MagicLink);
        await DeleteUserAsync(account.UserId);

        var response = await PostAsync("/auth/magic-consume", new { token = raw });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "user_not_found", "Account no longer exists.");
    }

    // ---- Six-digit codes ---------------------------------------------------

    [Fact]
    public async Task confirm_code_redeems_a_valid_code()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        await MintCodeAsync(account.UserId, VerificationPurposes.EmailVerificationCode, Code);

        var response = await PostAsync("/auth/verify-email/confirm-code", new { code = Code }, account.AccessToken);
        var json = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(json.GetProperty("user").TryGetProperty("emailVerifiedAt", out _));
    }

    [Fact]
    public async Task confirm_code_trims_before_matching_so_a_padded_code_still_redeems()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        await MintCodeAsync(account.UserId, VerificationPurposes.EmailVerificationCode, Code);

        // The trim runs BEFORE the regex, so this is a genuine redemption, not a
        // format rejection.
        var response = await PostAsync("/auth/verify-email/confirm-code", new { code = $"  {Code}  " }, account.AccessToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task a_code_is_single_use()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        await MintCodeAsync(account.UserId, VerificationPurposes.EmailVerificationCode, Code);

        Assert.Equal(
            HttpStatusCode.OK,
            (await PostAsync("/auth/verify-email/confirm-code", new { code = Code }, account.AccessToken)).StatusCode);

        var replay = await PostAsync("/auth/verify-email/confirm-code", new { code = Code }, account.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        await AssertErrorAsync(replay, "invalid_code", "That code is wrong or has expired. Send a new one.");

        // The REJECTED flavour carries no details; only the FORMAT flavour does.
        Assert.False((await ReadAsync(replay)).GetProperty("error").TryGetProperty("details", out _));
    }

    /// <summary>
    /// The security reason the code preimage includes the user id. Six digits is a
    /// small space, so a code minted for one account must be inert against another
    /// even when the attacker guesses the digits exactly right.
    /// </summary>
    [Fact]
    public async Task binds_a_code_to_the_account_it_was_minted_for()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var victim = await SignUpAsync();
        var attacker = await SignUpAsync();

        await MintCodeAsync(victim.UserId, VerificationPurposes.EmailVerificationCode, Code);

        // The attacker presents the victim's exact digits against their own account.
        var response = await PostAsync("/auth/verify-email/confirm-code", new { code = Code }, attacker.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "invalid_code", "That code is wrong or has expired. Send a new one.");
    }

    /// <summary>
    /// A code is also bound to its purpose, so an email-change code cannot be
    /// replayed as an email-verification code.
    /// </summary>
    [Fact]
    public async Task a_code_minted_for_another_purpose_is_rejected()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        await MintCodeAsync(account.UserId, VerificationPurposes.EmailChange, Code);

        var response = await PostAsync("/auth/verify-email/confirm-code", new { code = Code }, account.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "invalid_code", "That code is wrong or has expired. Send a new one.");
    }

    /// <summary>
    /// Issuing a code DELETES the previous unconsumed one, so a resend narrows the
    /// guessing window instead of widening it.
    /// </summary>
    [Fact]
    public async Task issuing_a_new_code_invalidates_the_previous_one()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        await MintCodeAsync(account.UserId, VerificationPurposes.EmailVerificationCode, Code);

        var resend = await PostAsync("/auth/verify-email/send-code", new { }, account.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, resend.StatusCode);

        var stale = await PostAsync("/auth/verify-email/confirm-code", new { code = Code }, account.AccessToken);

        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        await AssertErrorAsync(stale, "invalid_code", "That code is wrong or has expired. Send a new one.");
    }

    // ---- Email change, step 2 ---------------------------------------------

    [Fact]
    public async Task change_email_confirm_swaps_the_address_and_verifies_it()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();
        var target = NewEmail();

        await PostAsync("/auth/change-email", new { newEmail = target, password = Password }, account.AccessToken);
        await MintCodeAsync(account.UserId, VerificationPurposes.EmailChange, Code);

        var response = await PostAsync("/auth/change-email/confirm", new { code = Code }, account.AccessToken);
        var user = (await ReadAsync(response)).GetProperty("user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(target, user.GetProperty("email").GetString());

        // pendingEmail is UNSET, so the key must be gone — not present and null.
        Assert.False(user.TryGetProperty("pendingEmail", out _));

        // Redeeming a code sent to that address is proof of control.
        Assert.True(user.TryGetProperty("emailVerifiedAt", out _));

        // And the credential store moved with it.
        Assert.Equal(
            HttpStatusCode.OK,
            (await PostAsync("/auth/signin", new { email = target, password = Password })).StatusCode);
    }

    /// <summary>
    /// The opposite choice from change-password: with no <c>refreshToken</c> in the
    /// body this route revokes NOTHING, rather than signing the caller out
    /// everywhere.
    /// </summary>
    [Fact]
    public async Task change_email_confirm_without_a_refresh_token_revokes_nothing()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var account = await SignUpAsync();

        await PostAsync("/auth/change-email", new { newEmail = NewEmail(), password = Password }, account.AccessToken);
        await MintCodeAsync(account.UserId, VerificationPurposes.EmailChange, Code);
        await PostAsync("/auth/change-email/confirm", new { code = Code }, account.AccessToken);

        var refresh = await PostAsync("/auth/refresh", new { refreshToken = account.RefreshToken });

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    // ---- Helpers -----------------------------------------------------------

    private sealed record Account(string Email, ObjectId UserId, string AccessToken, string RefreshToken);

    private static string NewEmail() => $"redeem-{Guid.NewGuid():N}@probe.test";

    private async Task<Account> SignUpAsync()
    {
        var email = NewEmail();
        var json = await ReadAsync(await PostAsync("/auth/signup", new { email, password = Password }));
        var tokens = json.GetProperty("tokens");

        return new Account(
            email,
            ObjectId.Parse(json.GetProperty("user").GetProperty("id").GetString()!),
            tokens.GetProperty("accessToken").GetString()!,
            tokens.GetProperty("refreshToken").GetString()!);
    }

    private IMongoDatabase Database() =>
        _factory.Services.CreateScope().ServiceProvider.GetRequiredService<IMongoDatabase>();

    /// <summary>
    /// Mints a mailed-LINK token: the stored hash is <c>sha256(raw)</c> with no
    /// account mixed in. Returns the raw secret the mail would have carried.
    /// </summary>
    private async Task<string> MintLinkTokenAsync(ObjectId userId, string purpose, DateTime? expiresAt = null)
    {
        // tokenHash carries a unique index, so every token needs its own secret.
        var raw = $"probe-{Guid.NewGuid():N}{Guid.NewGuid():N}";
        await InsertAsync(userId, AuthTokenHashing.HashToken(raw), purpose, expiresAt);
        return raw;
    }

    /// <summary>
    /// Mints a 6-digit CODE: the stored hash is
    /// <c>sha256("&lt;userId&gt;:&lt;purpose&gt;:&lt;code&gt;")</c>.
    /// </summary>
    private Task MintCodeAsync(ObjectId userId, string purpose, string code, DateTime? expiresAt = null) =>
        InsertAsync(userId, AuthTokenHashing.HashCode(userId, purpose, code), purpose, expiresAt);

    private async Task InsertAsync(ObjectId userId, string tokenHash, string purpose, DateTime? expiresAt)
    {
        var now = DateTime.UtcNow;

        await Database()
            .GetCollection<VerificationTokenDocument>(MongoCollections.VerificationTokens)
            .InsertOneAsync(new VerificationTokenDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                TokenHash = tokenHash,
                Purpose = purpose,
                ExpiresAt = expiresAt ?? now.AddHours(1),
                CreatedAt = now,
                UpdatedAt = now,
            });
    }

    private Task DeleteUserAsync(ObjectId userId) =>
        Database()
            .GetCollection<MongoDB.Bson.BsonDocument>(MongoCollections.Users)
            .DeleteOneAsync(new MongoDB.Bson.BsonDocument("_id", userId));

    private Task<HttpResponseMessage> PostAsync(string path, object body, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return _factory.CreateApiClient().SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertErrorAsync(HttpResponseMessage response, string code, string message)
    {
        var error = (await ReadAsync(response)).GetProperty("error");

        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal(message, error.GetProperty("message").GetString());
    }

    /// <summary>
    /// Skip rather than fail when the parity Mongo instance is not running, matching
    /// the convention in <c>AuthEndpointTests</c> and the kernel's quota tests.
    /// </summary>
    private static async Task<bool> MongoAvailableAsync()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(AuthWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(750);

            await new MongoClient(settings)
                .GetDatabase("admin")
                .RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1));

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
