using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Auth;

/// <summary>
/// End-to-end tests for the auth slice, driven through the real HTTP pipeline.
///
/// <para>
/// These run against the parity Mongo instance and a private SQLite Identity
/// database per factory, so they exercise the REAL credential store rather than
/// a double — signup genuinely writes an Identity row and signin genuinely
/// verifies a hash. Before the SQLite seam existed the suite could not touch SQL
/// at all, which meant a green run said nothing about credential readiness.
/// </para>
///
/// <para>
/// Every assertion here is a line of the frozen contract
/// (<c>docs/contract/paths.auth.yaml</c>). Literal messages are compared
/// verbatim because the parity harness compares them byte for byte.
/// </para>
/// </summary>
public sealed class AuthEndpointTests : IClassFixture<AuthWebApplicationFactory>
{
    private readonly AuthWebApplicationFactory _factory;

    public AuthEndpointTests(AuthWebApplicationFactory factory) => _factory = factory;

    // ---- Signup ------------------------------------------------------------

    [Fact]
    public async Task signup_creates_an_account_and_returns_user_and_tokens()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var email = NewEmail();
        var response = await PostAsync("/auth/signup", new { email, password = "password123" });
        var json = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var user = json.GetProperty("user");
        Assert.Equal(email, user.GetProperty("email").GetString());
        Assert.True(user.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(24, user.GetProperty("id").GetString()!.Length);

        // Signup does not verify, so the key must be ABSENT — not null.
        Assert.False(user.TryGetProperty("emailVerifiedAt", out _));
        Assert.False(user.TryGetProperty("pendingEmail", out _));

        // The transform deletes the hash; it must never reach the wire.
        Assert.False(user.TryGetProperty("passwordHash", out _));

        var tokens = json.GetProperty("tokens");
        Assert.Equal(3, tokens.GetProperty("accessToken").GetString()!.Split('.').Length);
        Assert.Equal(43, tokens.GetProperty("refreshToken").GetString()!.Length);
    }

    /// <summary>
    /// The whole point of the SQLite seam: a real Identity row is written and a
    /// real hash is verified. A wrong password must fail against the same store.
    /// </summary>
    [Fact]
    public async Task signin_verifies_a_real_credential_stored_in_identity()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var email = NewEmail();
        await PostAsync("/auth/signup", new { email, password = "password123" });

        var good = await PostAsync("/auth/signin", new { email, password = "password123" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        var bad = await PostAsync("/auth/signin", new { email, password = "not-the-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        await AssertErrorAsync(bad, "invalid_credentials", "Wrong email or password.");
    }

    /// <summary>
    /// Identity's default policy demands an uppercase letter, a digit and a symbol.
    /// Node requires only length 8..128, so a lowercase-only password must be
    /// accepted — this is why the credential store bypasses the validator pipeline.
    /// </summary>
    [Fact]
    public async Task signup_accepts_a_password_that_identitys_default_policy_would_reject()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var email = NewEmail();
        var response = await PostAsync("/auth/signup", new { email, password = "onlylowercase" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var signin = await PostAsync("/auth/signin", new { email, password = "onlylowercase" });
        Assert.Equal(HttpStatusCode.OK, signin.StatusCode);
    }

    [Fact]
    public async Task signup_strips_unknown_keys_rather_than_rejecting_them()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        // The zod object is not .strict(), so an extra member is stripped and the
        // request still succeeds.
        var response = await PostAsync(
            "/auth/signup",
            new { email = NewEmail(), password = "password123", bogus = 1 });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task signup_rejects_a_duplicate_email_with_409()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var email = NewEmail();
        await PostAsync("/auth/signup", new { email, password = "password123" });

        var response = await PostAsync("/auth/signup", new { email, password = "password123" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertErrorAsync(response, "email_taken", "An account with this email already exists.");
    }

    // ---- Validation order --------------------------------------------------

    /// <summary>
    /// <c>.email()</c> runs BEFORE <c>.trim()</c>, so a padded address is rejected
    /// rather than trimmed into validity. The "obvious" implementation that
    /// normalises first would accept this and diverge silently.
    /// </summary>
    [Fact]
    public async Task a_padded_email_is_rejected_because_the_format_check_runs_before_the_trim()
    {
        var response = await PostAsync("/auth/signup", new { email = "  a@b.com  ", password = "password123" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = (await ReadAsync(response)).GetProperty("error");
        Assert.Equal("validation_error", error.GetProperty("code").GetString());
        Assert.Equal("Request validation failed", error.GetProperty("message").GetString());

        var details = error.GetProperty("details");
        Assert.Equal("email", details[0].GetProperty("path").GetString());
        Assert.Equal("Invalid email", details[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task a_mixed_case_email_is_accepted_and_lowercased()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var email = NewEmail();
        var response = await PostAsync("/auth/signup", new { email = email.ToUpperInvariant(), password = "password123" });
        var json = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(email, json.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task validation_reports_every_issue_in_schema_order()
    {
        var response = await PostAsync("/auth/signup", new { email = "not-an-email", password = "short" });
        var details = (await ReadAsync(response)).GetProperty("error").GetProperty("details");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // zod accumulates rather than short-circuiting, and walks the shape in
        // declaration order: email before password.
        Assert.Equal(2, details.GetArrayLength());
        Assert.Equal("email", details[0].GetProperty("path").GetString());
        Assert.Equal("Invalid email", details[0].GetProperty("message").GetString());
        Assert.Equal("password", details[1].GetProperty("path").GetString());
        Assert.Equal(
            "String must contain at least 8 character(s)",
            details[1].GetProperty("message").GetString());
    }

    [Fact]
    public async Task a_missing_field_reports_Required_and_a_wrong_type_reports_the_received_type()
    {
        var missing = await PostAsync("/auth/signup", new { password = "password123" });
        var missingDetails = (await ReadAsync(missing)).GetProperty("error").GetProperty("details");
        Assert.Equal("Required", missingDetails[0].GetProperty("message").GetString());

        var wrongType = await PostRawAsync("/auth/signup", """{"email":42,"password":"password123"}""");
        var wrongDetails = (await ReadAsync(wrongType)).GetProperty("error").GetProperty("details");
        Assert.Equal("Expected string, received number", wrongDetails[0].GetProperty("message").GetString());
    }

    // ---- The two details shapes -------------------------------------------

    /// <summary>
    /// confirm-code mirrors <c>safeParse</c>, so its format failure carries the
    /// FLATTEN shape under the route's own code — not <c>validation_error</c> with
    /// an issue array. Picking the wrong shape is a silent parity break.
    /// </summary>
    [Fact]
    public async Task a_malformed_code_returns_invalid_code_with_flatten_details()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await PostAsync("/auth/verify-email/confirm-code", new { code = "abc" }, session.AccessToken);
        var error = (await ReadAsync(response)).GetProperty("error");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_code", error.GetProperty("code").GetString());
        Assert.Equal("Enter the 6-digit code.", error.GetProperty("message").GetString());

        var details = error.GetProperty("details");
        Assert.Equal(0, details.GetProperty("formErrors").GetArrayLength());
        Assert.Equal(
            "Enter the 6-digit code.",
            details.GetProperty("fieldErrors").GetProperty("code")[0].GetString());
    }

    /// <summary>
    /// The second <c>invalid_code</c>: a well-formed but unusable code. Different
    /// message, and NO details at all.
    /// </summary>
    [Fact]
    public async Task a_rejected_code_returns_invalid_code_with_no_details()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await PostAsync("/auth/verify-email/confirm-code", new { code = "000000" }, session.AccessToken);
        var error = (await ReadAsync(response)).GetProperty("error");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_code", error.GetProperty("code").GetString());
        Assert.Equal("That code is wrong or has expired. Send a new one.", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("details", out _));
    }

    /// <summary>Trim runs BEFORE the regex on codes — the opposite order to email.</summary>
    [Fact]
    public async Task a_padded_code_passes_the_format_check_because_the_trim_runs_first()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await PostAsync("/auth/verify-email/confirm-code", new { code = " 424242 " }, session.AccessToken);
        var error = (await ReadAsync(response)).GetProperty("error");

        // Reaching the "wrong code" branch proves the format check passed.
        Assert.Equal("That code is wrong or has expired. Send a new one.", error.GetProperty("message").GetString());
        Assert.False(error.TryGetProperty("details", out _));
    }

    // ---- Refresh rotation --------------------------------------------------

    [Fact]
    public async Task refresh_rotates_the_token_and_returns_tokens_without_a_user()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await PostAsync("/auth/refresh", new { refreshToken = session.RefreshToken });
        var json = await ReadAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(json.TryGetProperty("user", out _));
        Assert.NotEqual(session.RefreshToken, json.GetProperty("tokens").GetProperty("refreshToken").GetString());
    }

    /// <summary>
    /// Replaying a rotated-out token is treated as a leak: the whole family is
    /// revoked, so the successor issued a moment earlier stops working too.
    /// </summary>
    [Fact]
    public async Task replaying_a_rotated_token_revokes_the_entire_family()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();

        var rotated = await ReadAsync(await PostAsync("/auth/refresh", new { refreshToken = session.RefreshToken }));
        var successor = rotated.GetProperty("tokens").GetProperty("refreshToken").GetString();

        // Replay the dead token.
        var replay = await PostAsync("/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        await AssertErrorAsync(replay, "invalid_refresh_token", "This session has expired. Please sign in again.");

        // The successor is now collateral damage — that is the point.
        var afterFamilyRevoke = await PostAsync("/auth/refresh", new { refreshToken = successor });
        Assert.Equal(HttpStatusCode.Unauthorized, afterFamilyRevoke.StatusCode);
    }

    [Fact]
    public async Task an_unknown_refresh_token_is_the_same_401_as_a_reused_one()
    {
        var response = await PostAsync("/auth/refresh", new { refreshToken = "not-a-real-refresh-token" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertErrorAsync(response, "invalid_refresh_token", "This session has expired. Please sign in again.");
    }

    // ---- Sessions ----------------------------------------------------------

    [Fact]
    public async Task sessions_list_marks_only_the_supplied_token_as_current()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();

        var withToken = await ReadAsync(await PostAsync(
            "/auth/sessions/list",
            new { refreshToken = session.RefreshToken },
            session.AccessToken));

        var only = withToken.GetProperty("sessions")[0];
        Assert.True(only.GetProperty("current").GetBoolean());
        Assert.Equal(24, only.GetProperty("id").GetString()!.Length);

        // Withheld projection: none of the storage columns may leak.
        Assert.False(only.TryGetProperty("tokenHash", out _));
        Assert.False(only.TryGetProperty("userId", out _));
        Assert.False(only.TryGetProperty("expiresAt", out _));

        var withoutToken = await ReadAsync(await PostAsync("/auth/sessions/list", new { }, session.AccessToken));
        Assert.False(withoutToken.GetProperty("sessions")[0].GetProperty("current").GetBoolean());
    }

    [Fact]
    public async Task revoking_an_unknown_or_malformed_session_id_is_the_same_404()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();

        foreach (var id in new[] { "6a78c437aa461ae1dc64ffff", "zzz" })
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"/auth/sessions/{id}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);

            var response = await _factory.CreateApiClient().SendAsync(request);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            await AssertErrorAsync(response, "session_not_found", "That session is no longer active.");
        }
    }

    /// <summary>A malformed body still yields 204 — signout swallows its parse failure.</summary>
    [Fact]
    public async Task signout_returns_204_even_for_an_absent_or_unknown_token()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();

        var empty = await PostAsync("/auth/signout", new { }, session.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);

        var unknown = await PostAsync("/auth/signout", new { refreshToken = "garbage" }, session.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);

        // 204 must carry no body and no content type.
        Assert.Equal(0, (await unknown.Content.ReadAsByteArrayAsync()).Length);
        Assert.Null(unknown.Content.Headers.ContentType);
    }

    // ---- /auth/me ----------------------------------------------------------

    [Fact]
    public async Task me_returns_the_composed_user()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await GetAsync("/auth/me", session.AccessToken);
        var user = (await ReadAsync(response)).GetProperty("user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(session.Email, user.GetProperty("email").GetString());

        // Defaults that the profile document supplies, all always present.
        Assert.Equal("system", user.GetProperty("theme").GetString());
        Assert.Equal("md", user.GetProperty("textSize").GetString());
        Assert.False(user.GetProperty("hasOnboarded").GetBoolean());
        Assert.Equal("free", user.GetProperty("subscription").GetProperty("tier").GetString());
        Assert.True(user.GetProperty("notifications").GetProperty("push").GetBoolean());
        Assert.Equal("09:00", user.GetProperty("imports").GetProperty("defaultTimeOfDay").GetString());
        Assert.Equal(6, user.GetProperty("preferredDomains").GetArrayLength());
    }

    [Fact]
    public async Task me_distinguishes_a_missing_header_from_a_bad_token()
    {
        var missing = await _factory.CreateApiClient().GetAsync("/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        await AssertErrorAsync(missing, "missing_token", "Missing access token");

        // A lowercase scheme does not match the exact "Bearer " prefix, so it is
        // missing_token rather than invalid_token.
        var lowercase = new HttpRequestMessage(HttpMethod.Get, "/auth/me");
        lowercase.Headers.TryAddWithoutValidation("Authorization", "bearer something");
        await AssertErrorAsync(await _factory.CreateApiClient().SendAsync(lowercase), "missing_token", "Missing access token");

        var garbage = await GetAsync("/auth/me", "not.a.jwt");
        await AssertErrorAsync(garbage, "invalid_token", "Invalid or expired access token");
    }

    /// <summary>A valid token whose account is gone is 404, a different branch from a bad token.</summary>
    [Fact]
    public async Task me_returns_404_when_the_account_has_been_deleted()
    {
        var token = KernelPipelineTests.NodeShapedToken("6a78c437aa461ae1dc64ffff", "ghost@example.com");
        var response = await GetAsync("/auth/me", token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertErrorAsync(response, "user_not_found", "Account no longer exists.");
    }

    // ---- Enumeration safety ------------------------------------------------

    [Fact]
    public async Task forgot_password_returns_204_whether_or_not_the_account_exists()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var known = await SignUpAsync();

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await PostAsync("/auth/forgot-password", new { email = known.Email })).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await PostAsync("/auth/forgot-password", new { email = NewEmail() })).StatusCode);
    }

    // ---- Change password ---------------------------------------------------

    [Fact]
    public async Task change_password_reports_the_refine_through_the_validation_error_lane()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await PostAsync(
            "/auth/change-password",
            new { currentPassword = "password123", newPassword = "password123" },
            session.AccessToken);

        var error = (await ReadAsync(response)).GetProperty("error");

        // The cross-field refine travels the GLOBAL ZodError lane, so it is
        // validation_error with an ISSUE ARRAY — not invalid_body with flatten.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", error.GetProperty("code").GetString());

        var details = error.GetProperty("details");
        Assert.Equal("newPassword", details[0].GetProperty("path").GetString());
        Assert.Equal(
            "New password must be different from your current one.",
            details[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task change_password_rejects_a_wrong_current_password_with_its_own_message()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await PostAsync(
            "/auth/change-password",
            new { currentPassword = "wrong-password", newPassword = "brand-new-password" },
            session.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The same code as signin, a DIFFERENT message.
        await AssertErrorAsync(response, "invalid_credentials", "Your current password is incorrect.");
    }

    // ---- Magic link --------------------------------------------------------

    [Fact]
    public async Task magic_link_auto_creates_a_passwordless_account_and_always_returns_204()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var email = NewEmail();
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/auth/magic-link", new { email })).StatusCode);

        // The account now exists with no credential, so signin folds into the
        // generic 401 rather than reporting "no password".
        var signin = await PostAsync("/auth/signin", new { email, password = "anything-at-all" });
        Assert.Equal(HttpStatusCode.Unauthorized, signin.StatusCode);
        await AssertErrorAsync(signin, "invalid_credentials", "Wrong email or password.");
    }

    [Fact]
    public async Task magic_consume_rejects_an_unknown_token()
    {
        var response = await PostAsync("/auth/magic-consume", new { token = "not-a-real-magic-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(
            response,
            "invalid_magic_token",
            "This sign-in link is invalid or has expired. Request a new one.");
    }

    [Fact]
    public async Task verify_email_rejects_an_unknown_token()
    {
        var response = await PostAsync("/auth/verify-email", new { token = "not-a-real-verification-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(
            response,
            "invalid_verification_token",
            "This verification link is invalid or has expired.");
    }

    // ---- Email change ------------------------------------------------------

    [Fact]
    public async Task change_email_parks_a_pending_address_without_moving_the_current_one()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var newEmail = NewEmail();

        var response = await PostAsync(
            "/auth/change-email",
            new { newEmail, password = "password123" },
            session.AccessToken);

        var user = (await ReadAsync(response)).GetProperty("user");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(session.Email, user.GetProperty("email").GetString());
        Assert.Equal(newEmail, user.GetProperty("pendingEmail").GetString());

        // Cancelling clears it and the key disappears again — omitted, not null.
        var request = new HttpRequestMessage(HttpMethod.Delete, "/auth/change-email");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        Assert.Equal(HttpStatusCode.NoContent, (await _factory.CreateApiClient().SendAsync(request)).StatusCode);

        var after = (await ReadAsync(await GetAsync("/auth/me", session.AccessToken))).GetProperty("user");
        Assert.False(after.TryGetProperty("pendingEmail", out _));
    }

    [Fact]
    public async Task change_email_to_the_current_address_is_rejected_before_the_password_check()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await PostAsync(
            "/auth/change-email",
            new { newEmail = session.Email, password = "wrong-password" },
            session.AccessToken);

        // email_unchanged wins because it is checked BEFORE the password.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "email_unchanged", "That's already your email address.");
    }

    [Fact]
    public async Task change_email_requires_the_password_when_the_account_has_one()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();

        var missing = await PostAsync("/auth/change-email", new { newEmail = NewEmail() }, session.AccessToken);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        await AssertErrorAsync(missing, "password_required", "Enter your password to change your email.");

        var wrong = await PostAsync(
            "/auth/change-email",
            new { newEmail = NewEmail(), password = "wrong-password" },
            session.AccessToken);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        // The THIRD invalid_credentials message.
        await AssertErrorAsync(wrong, "invalid_credentials", "That password is incorrect.");
    }

    [Fact]
    public async Task change_email_confirm_rejects_a_confirm_with_nothing_pending()
    {
        if (!await MongoAvailableAsync())
        {
            return;
        }

        var session = await SignUpAsync();
        var response = await PostAsync("/auth/change-email/confirm", new { code = "424242" }, session.AccessToken);

        // no_pending_email is checked BEFORE the code, so the code is never consumed.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertErrorAsync(response, "no_pending_email", "There's no email change waiting.");
    }

    // ---- Framework edges ---------------------------------------------------

    /// <summary>
    /// body-parser's SyntaxError is not recognised by the reference's error
    /// handler, so it falls through to the generic 500. ASP.NET would answer 400;
    /// reproducing the 500 is deliberate.
    /// </summary>
    [Fact]
    public async Task malformed_json_is_500_not_400()
    {
        var response = await PostRawAsync("/auth/signup", """{"email":"a@b.com","passw""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertErrorAsync(response, "internal_error", "Internal server error");
    }

    [Fact]
    public async Task an_oversized_body_is_500_not_413()
    {
        var padding = new string('a', 300 * 1024);
        var response = await PostRawAsync("/auth/signup", $$"""{"email":"a@b.com","password":"{{padding}}"}""");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertErrorAsync(response, "internal_error", "Internal server error");
    }

    [Fact]
    public async Task every_authenticated_auth_route_rejects_an_anonymous_call_with_401()
    {
        var client = _factory.CreateApiClient();

        var probes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, "/auth/me"),
            (HttpMethod.Post, "/auth/signout"),
            (HttpMethod.Post, "/auth/sessions/list"),
            (HttpMethod.Post, "/auth/sessions/revoke-others"),
            (HttpMethod.Delete, "/auth/sessions/6a78c437aa461ae1dc64ffff"),
            (HttpMethod.Post, "/auth/change-password"),
            (HttpMethod.Post, "/auth/verify-email/send-code"),
            (HttpMethod.Post, "/auth/verify-email/confirm-code"),
            (HttpMethod.Post, "/auth/change-email"),
            (HttpMethod.Delete, "/auth/change-email"),
            (HttpMethod.Post, "/auth/change-email/confirm"),
        };

        foreach (var (method, path) in probes)
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, path));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            await AssertErrorAsync(response, "missing_token", "Missing access token");
        }
    }

    // ---- Helpers -----------------------------------------------------------

    private sealed record Session(string Email, string AccessToken, string RefreshToken);

    private static string NewEmail() => $"auth-{Guid.NewGuid():N}@probe.test";

    private async Task<Session> SignUpAsync()
    {
        var email = NewEmail();
        var json = await ReadAsync(await PostAsync("/auth/signup", new { email, password = "password123" }));
        var tokens = json.GetProperty("tokens");

        return new Session(
            email,
            tokens.GetProperty("accessToken").GetString()!,
            tokens.GetProperty("refreshToken").GetString()!);
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return _factory.CreateApiClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> PostRawAsync(string path, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        return _factory.CreateApiClient().SendAsync(request);
    }

    private Task<HttpResponseMessage> GetAsync(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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
    /// Mongo-backed tests return early rather than failing when the parity instance
    /// is not running, so the suite stays green on a machine without it — the same
    /// convention the kernel's quota tests use.
    /// </summary>
    private static async Task<bool> MongoAvailableAsync()
    {
        try
        {
            var settings = MongoClientSettings.FromConnectionString(AuthWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromMilliseconds(750);

            await new MongoClient(settings)
                .GetDatabase("admin")
                .RunCommandAsync<MongoDB.Bson.BsonDocument>(new MongoDB.Bson.BsonDocument("ping", 1));

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Points the suite at this slice's own parity database so a run cannot collide
/// with another slice's fixtures.
/// </summary>
public sealed class AuthWebApplicationFactory : KernelWebApplicationFactory
{
    public AuthWebApplicationFactory() => With("MongoDbSettings:DatabaseName", "kitto_parity_dotnet_b");
}
