using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using Life_Admin_Autopilot.Tests.Kernel;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Features.Tasks;

public sealed class FinancialInsightsTests : IClassFixture<TasksWebApplicationFactory>
{
    private readonly TasksWebApplicationFactory _factory;

    public FinancialInsightsTests(TasksWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task rejects_anonymous_requests_with_401()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/me/financial-insights");
        var response = await _factory.CreateApiClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task returns_empty_aggregations_when_no_tasks_exist()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return; // Skip if Mongo test instance is offline
        }

        var userId = ObjectId.GenerateNewId();

        var json = await GetJsonAsync("/me/financial-insights", HttpStatusCode.OK, userId);

        Assert.Equal(0, json.GetProperty("overdueCount").GetInt32());
        Assert.Equal(0, json.GetProperty("nearTermCount").GetInt32());
        Assert.Equal(0, json.GetProperty("undatedCount").GetInt32());
        Assert.Equal(0, json.GetProperty("urgentCount").GetInt32());

        Assert.Empty(json.GetProperty("overdueTasks").EnumerateArray());
        Assert.Empty(json.GetProperty("nearTermTasks").EnumerateArray());
        Assert.Empty(json.GetProperty("undatedTasks").EnumerateArray());
        Assert.Empty(json.GetProperty("urgentTasks").EnumerateArray());
    }

    [Fact]
    public async Task aggregates_financial_tasks_by_due_dates_and_priority()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        // 1. Seed Overdue Finance Task
        await SeedTaskAsync(tasks, userId, "Overdue Bill", t =>
        {
            t["domain"] = "finance";
            t["dueAt"] = now.AddHours(-2);
        });

        // 2. Seed Near-Term Finance Task
        await SeedTaskAsync(tasks, userId, "Near Term Bill", t =>
        {
            t["domain"] = "finance";
            t["dueAt"] = now.AddDays(5);
        });

        // 3. Seed Undated Finance Task
        await SeedTaskAsync(tasks, userId, "Undated Goal", t =>
        {
            t["domain"] = "finance";
            t.Remove("dueAt");
        });

        // 4. Seed Urgent (but far-term) Finance Task
        await SeedTaskAsync(tasks, userId, "Urgent Far Bill", t =>
        {
            t["domain"] = "finance";
            t["priority"] = "urgent";
            t["dueAt"] = now.AddDays(20);
        });

        // 5. Seed Non-Finance Domain Task (should be ignored)
        await SeedTaskAsync(tasks, userId, "Health Task", t =>
        {
            t["domain"] = "health";
            t["dueAt"] = now.AddDays(1);
        });

        // 6. Seed Completed Finance Task (should be ignored)
        await SeedTaskAsync(tasks, userId, "Completed Bill", t =>
        {
            t["domain"] = "finance";
            t["status"] = "done";
            t["dueAt"] = now.AddDays(1);
        });

        // Act
        var json = await GetJsonAsync("/me/financial-insights", HttpStatusCode.OK, userId);

        // Assert
        Assert.Equal(1, json.GetProperty("overdueCount").GetInt32());
        Assert.Equal(1, json.GetProperty("nearTermCount").GetInt32());
        Assert.Equal(1, json.GetProperty("undatedCount").GetInt32());
        Assert.Equal(1, json.GetProperty("urgentCount").GetInt32());

        var overdueList = json.GetProperty("overdueTasks").EnumerateArray().ToList();
        Assert.Single(overdueList);
        Assert.Equal("Overdue Bill", overdueList[0].GetProperty("title").GetString());

        var nearTermList = json.GetProperty("nearTermTasks").EnumerateArray().ToList();
        Assert.Single(nearTermList);
        Assert.Equal("Near Term Bill", nearTermList[0].GetProperty("title").GetString());

        var undatedList = json.GetProperty("undatedTasks").EnumerateArray().ToList();
        Assert.Single(undatedList);
        Assert.Equal("Undated Goal", undatedList[0].GetProperty("title").GetString());

        var urgentList = json.GetProperty("urgentTasks").EnumerateArray().ToList();
        Assert.Single(urgentList);
        Assert.Equal("Urgent Far Bill", urgentList[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task enforces_tenancy_boundary_for_financial_insights()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userA = ObjectId.GenerateNewId();
        var userB = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        // Seed task for User A
        await SeedTaskAsync(tasks, userA, "User A Bill", t =>
        {
            t["domain"] = "finance";
            t["dueAt"] = now.AddHours(-1);
        });

        // Query for User B
        var json = await GetJsonAsync("/me/financial-insights", HttpStatusCode.OK, userB);

        // Assert
        Assert.Equal(0, json.GetProperty("overdueCount").GetInt32());
        Assert.Empty(json.GetProperty("overdueTasks").EnumerateArray());
    }

    [Fact]
    public async Task ignores_soft_deleted_tasks()
    {
        var tasks = TryGetTasks();
        if (tasks is null)
        {
            return;
        }

        var userId = ObjectId.GenerateNewId();
        var now = DateTime.UtcNow;

        await SeedTaskAsync(tasks, userId, "Deleted Bill", t =>
        {
            t["domain"] = "finance";
            t["dueAt"] = now.AddHours(-1);
            t["deletedAt"] = now;
        });

        // Query
        var json = await GetJsonAsync("/me/financial-insights", HttpStatusCode.OK, userId);

        // Assert
        Assert.Equal(0, json.GetProperty("overdueCount").GetInt32());
    }

    // ---- HTTP Helpers ----

    private async Task<JsonElement> GetJsonAsync(string path, HttpStatusCode expected, ObjectId userId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        var token = KernelPipelineTests.NodeShapedToken(userId.ToString(), $"{userId}@example.test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _factory.CreateApiClient().SendAsync(request);
        Assert.Equal(expected, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static async Task<ObjectId> SeedTaskAsync(
        IMongoCollection<BsonDocument> tasks,
        ObjectId userId,
        string title,
        Action<BsonDocument>? customise = null)
    {
        var now = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var task = new BsonDocument
        {
            ["_id"] = ObjectId.GenerateNewId(),
            ["userId"] = userId,
            ["title"] = title,
            ["domain"] = "home",
            ["kind"] = "list",
            ["status"] = "open",
            ["priority"] = "normal",
            ["subtasks"] = new BsonArray(),
            ["tags"] = new BsonArray(),
            ["reminders"] = new BsonArray(),
            ["rescheduleCount"] = 0,
            ["createdAt"] = now,
            ["updatedAt"] = now,
            ["__v"] = 0,
        };

        customise?.Invoke(task);
        await tasks.InsertOneAsync(task);
        return task["_id"].AsObjectId;
    }

    private static IMongoCollection<BsonDocument>? TryGetTasks() =>
        TryGetDatabase()?.GetCollection<BsonDocument>(MongoCollections.Tasks);

    private static IMongoDatabase? TryGetDatabase()
    {
        MongoKernelConventions.Register();
        try
         {
             var settings = MongoClientSettings.FromConnectionString(KernelWebApplicationFactory.ParityMongoUri);
             settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);
 
             var database = new MongoClient(settings)
                 .GetDatabase(TasksWebApplicationFactory.TasksDatabase);
             database.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
 
             return database;
         }
         catch (Exception)
         {
             return null;
         }
    }
}
