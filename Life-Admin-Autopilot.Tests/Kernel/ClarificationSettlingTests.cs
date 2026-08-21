using Life_Admin_Autopilot.BLL.Kernel.Tasks;
using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.Tests.Kernel;

/// <summary>
/// Settling a date question by answering it with an edit rather than an answer.
///
/// <para>
/// The undated-matter pop-up sends the user to the matter, because picking a date
/// there gets the app's real editor and a clash surfaces the way it does everywhere
/// else. The cost of that choice is that the answer arrives on the task write path
/// as an ordinary edit, with nothing to tell the question it has been answered —
/// and the only safety net is a stale settler that waits SEVEN DAYS. These pin the
/// close, and just as importantly pin what it must NOT close.
/// </para>
///
/// <para>
/// Skips when the parity Mongo instance is unreachable, like every other
/// Mongo-backed suite here.
/// </para>
/// </summary>
public sealed class ClarificationSettlingTests
{
    private const string SettlingDatabase = "kitto_parity_dotnet_settling_tests";

    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task closes_the_date_question_for_that_matter()
    {
        var db = Database();
        if (db is null) return;

        var (userId, taskId) = (ObjectId.GenerateNewId(), ObjectId.GenerateNewId());
        await InsertAsync(db, userId, taskId, "date");

        var closed = await new ClarificationCascade(db).SettleDateQuestionsAsync(userId, taskId, Now);

        Assert.Equal(1, closed);

        var row = await FindAsync(db, userId, taskId);
        Assert.Equal("resolved", row!.Status);
        // Resolved, not dropped, and the answer says what happened: dropping would
        // record that the user declined to answer, which is the opposite.
        Assert.Equal(ClarificationCascade.AnsweredByEditing, row.Answer);
        Assert.Equal(Now, row.ResolvedAt);
    }

    [Fact]
    public async Task leaves_a_question_that_a_date_does_not_answer()
    {
        var db = Database();
        if (db is null) return;

        // "File this as well?" is not answered by a date arriving, and closing it
        // here would silently discard something the user has not addressed.
        var (userId, taskId) = (ObjectId.GenerateNewId(), ObjectId.GenerateNewId());
        await InsertAsync(db, userId, taskId, "confirm");

        var closed = await new ClarificationCascade(db).SettleDateQuestionsAsync(userId, taskId, Now);

        Assert.Equal(0, closed);
        Assert.Equal("open", (await FindAsync(db, userId, taskId))!.Status);
    }

    [Fact]
    public async Task leaves_another_matters_question_alone()
    {
        var db = Database();
        if (db is null) return;

        var userId = ObjectId.GenerateNewId();
        var mine = ObjectId.GenerateNewId();
        var theirs = ObjectId.GenerateNewId();
        await InsertAsync(db, userId, theirs, "date");

        Assert.Equal(0, await new ClarificationCascade(db).SettleDateQuestionsAsync(userId, mine, Now));
        Assert.Equal("open", (await FindAsync(db, userId, theirs))!.Status);
    }

    [Fact]
    public async Task closing_nothing_is_not_an_error()
    {
        var db = Database();
        if (db is null) return;

        // The ordinary case by a wide margin: most matters carry no question at all,
        // and this runs on every dated patch.
        var closed = await new ClarificationCascade(db)
            .SettleDateQuestionsAsync(ObjectId.GenerateNewId(), ObjectId.GenerateNewId(), Now);

        Assert.Equal(0, closed);
    }

    // ---- helpers ---------------------------------------------------------------

    private static Task InsertAsync(IMongoDatabase db, ObjectId userId, ObjectId taskId, string kind) =>
        db.GetCollection<ClarificationDocument>(MongoCollections.Clarifications)
            .InsertOneAsync(new ClarificationDocument
            {
                Id = ObjectId.GenerateNewId(),
                UserId = userId,
                TaskId = taskId,
                Kind = kind,
                Status = "open",
                Question = "When is it due?",
                CreatedAt = Now.AddMinutes(-1),
                UpdatedAt = Now.AddMinutes(-1),
            });

    private static async Task<ClarificationDocument?> FindAsync(
        IMongoDatabase db, ObjectId userId, ObjectId taskId) =>
        await db.GetCollection<ClarificationDocument>(MongoCollections.Clarifications)
            .Find(Builders<ClarificationDocument>.Filter.And(
                Builders<ClarificationDocument>.Filter.Eq(c => c.UserId, userId),
                Builders<ClarificationDocument>.Filter.Eq(c => c.TaskId, taskId)))
            .FirstOrDefaultAsync();

    private static IMongoDatabase? Database()
    {
        MongoKernelConventions.Register();

        try
        {
            var settings = MongoClientSettings.FromConnectionString(
                KernelWebApplicationFactory.ParityMongoUri);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(2);

            var db = new MongoClient(settings).GetDatabase(SettlingDatabase);
            db.RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            return db;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
