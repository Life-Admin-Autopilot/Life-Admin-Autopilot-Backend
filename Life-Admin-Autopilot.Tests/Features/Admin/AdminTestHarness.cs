using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.BLL.Features.Admin;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Push;
using Life_Admin_Autopilot.DAL.Push.Models;
using Life_Admin_Autopilot.DAL.Repositories;
using Life_Admin_Autopilot.Tests.Kernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Admin;

/// <summary>
/// Records every push it is handed and answers however the test tells it to.
///
/// <para>
/// The real <c>FcmPushNotificationService</c> needs a Firebase project and a
/// network. What the console's behaviour actually depends on is the SHAPE of the
/// answer — delivered, permanently-dead token, transient failure — so the tests
/// drive those three shapes directly rather than pretending to reach Google.
/// </para>
/// </summary>
public sealed class RecordingPushService : IPushNotificationService
{
    public List<PushNotificationRequest> Sent { get; } = new();

    /// <summary>Set per test. Null means every send succeeds.</summary>
    public Func<PushNotificationRequest, Result<PushNotificationResult>>? Responder { get; set; }

    public Task<Result<PushNotificationResult>> SendAsync(
        PushNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(request);

        var response = Responder?.Invoke(request)
            ?? Result<PushNotificationResult>.Success(new PushNotificationResult
            {
                MessageId = "test-message-id",
                LatencyMs = 1,
            });

        return Task.FromResult(response);
    }

    public void Reset()
    {
        Sent.Clear();
        Responder = null;
    }
}

/// <summary>
/// Its own database and its own admin signing secret, so a parallel slice's run
/// cannot see these rows and a console token minted here cannot be replayed
/// anywhere else.
/// </summary>
public sealed class AdminWebApplicationFactory : KernelWebApplicationFactory
{
    public const string AdminDatabase = "kitto_parity_dotnet_admin_tests";

    /// <summary>
    /// Deliberately different from <see cref="KernelWebApplicationFactory.TestJwtSecret"/>.
    /// Several tests assert that a customer token cannot authenticate against
    /// <c>/admin/*</c>; with a shared secret those tests would pass for the wrong
    /// reason (a missing role) rather than the right one (an invalid signature).
    /// </summary>
    public const string AdminSecret = "admin-console-test-secret-at-least-32-chars-long";

    public RecordingPushService Push { get; } = new();

    public AdminWebApplicationFactory()
    {
        With("MongoDbSettings:DatabaseName", AdminDatabase);
        With("ADMIN_JWT_SECRET", AdminSecret);

        // Langflow does not report a model, so without this every chat turn records
        // as unpriced and the cost assertions would all be zero.
        With("Ai:Pricing:DefaultChatModel", "gemini-2.5-flash");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPushNotificationService>();
            services.AddSingleton<IPushNotificationService>(Push);
        });
    }

    /// <summary>A console token for a given role set. Signed with the ADMIN key.</summary>
    public string AdminToken(
        Guid identityId,
        string email = "admin@test.local",
        params string[] roles)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AdminSecret));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, identityId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
        };

        claims.AddRange(
            (roles.Length == 0 ? new[] { AdminRoles.Admin } : roles)
            .Select(r => new Claim(ClaimTypes.Role, r)));

        var token = new JwtSecurityToken(
            issuer: "kitto-admin",
            audience: "kitto-admin-console",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public HttpClient AdminClient(params string[] roles)
    {
        var client = CreateApiClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AdminToken(Guid.NewGuid(), roles: roles));

        return client;
    }

    /// <summary>The Mongo database this factory's app is wired to.</summary>
    public IMongoDatabase Database()
    {
        var settings = MongoClientSettings.FromConnectionString(ParityMongoUri);
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

        return new MongoClient(settings).GetDatabase(AdminDatabase);
    }

    /// <summary>
    /// True when the parity Mongo is up. Tests that need rows skip when it is not,
    /// matching the convention the ICS and task repository suites already use.
    /// </summary>
    public bool MongoIsUp()
    {
        try
        {
            Database().RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>Shared row-seeding and JSON helpers.</summary>
internal static class AdminTestData
{
    public static async Task<ObjectId> SeedUserAsync(
        IMongoDatabase db,
        string email,
        Action<BsonDocument>? customise = null)
    {
        var id = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        var document = new BsonDocument
        {
            ["_id"] = id,
            ["identityUserId"] = Guid.NewGuid().ToString(),
            ["email"] = email,
            ["hasOnboarded"] = true,
            ["theme"] = "system",
            ["textSize"] = "md",
            ["subscription"] = new BsonDocument { ["tier"] = "free" },
            ["createdAt"] = now,
            ["updatedAt"] = now,
        };

        customise?.Invoke(document);

        await db.GetCollection<BsonDocument>("users").InsertOneAsync(document);
        return id;
    }

    public static Task SeedDeviceAsync(IMongoDatabase db, ObjectId userId, string token, string platform = "Ios") =>
        db.GetCollection<BsonDocument>("deviceTokens").InsertOneAsync(new BsonDocument
        {
            ["UserId"] = userId.ToString(),
            ["Token"] = token,
            ["Platform"] = platform,
            ["DeviceModel"] = "Test device",
            ["RegisteredAt"] = DateTime.UtcNow,
            ["LastSeenAt"] = DateTime.UtcNow,
            ["IsActive"] = true,
        });

    public static async Task ClearAsync(IMongoDatabase db, params string[] collections)
    {
        foreach (var name in collections)
        {
            await db.GetCollection<BsonDocument>(name).DeleteManyAsync(new BsonDocument());
        }
    }

    public static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public static Result<PushNotificationResult> Dead(string code) =>
        Result<PushNotificationResult>.Failure(new Error(code, "token is gone"));

    public static Result<PushNotificationResult> Transient() =>
        Result<PushNotificationResult>.Failure(new Error(PushErrorCodes.Unavailable, "try later"));
}
