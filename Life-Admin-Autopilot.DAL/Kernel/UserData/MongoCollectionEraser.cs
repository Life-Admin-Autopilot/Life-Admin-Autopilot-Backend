using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.UserData;

/// <summary>
/// The common case: "delete every document in collection X whose <c>userId</c> is
/// this user". Subclass it rather than reimplementing the delete.
/// </summary>
/// <example>
/// <code>
/// internal sealed class VoiceNoteEraser() : MongoCollectionEraser("voice-notes", MongoCollections.VoiceNotes);
/// </code>
/// </example>
public abstract class MongoCollectionEraser : IUserDataEraser
{
    private readonly string _collectionName;

    protected MongoCollectionEraser(string name, string collectionName, int order = UserErasureOrder.Dependents)
    {
        Name = name;
        _collectionName = collectionName;
        Order = order;
    }

    public string Name { get; }

    public int Order { get; }

    /// <summary>Injected by DI in the derived type; assigned through <see cref="UseDatabase"/>.</summary>
    protected IMongoDatabase Database { get; private set; } = null!;

    public MongoCollectionEraser UseDatabase(IMongoDatabase database)
    {
        Database = database;
        return this;
    }

    public virtual Task EraseAsync(UserErasureContext context, CancellationToken cancellationToken = default) =>
        Database
            .GetCollection<BsonDocument>(_collectionName)
            .DeleteManyAsync(Builders<BsonDocument>.Filter.Eq("userId", context.UserId), cancellationToken);
}

/// <summary>
/// Retires the account itself. Registered by the kernel at
/// <see cref="UserErasureOrder.Account"/> so it always runs last. Slices must not
/// register anything at that order.
/// </summary>
public sealed class UserProfileEraser : IUserDataEraser
{
    private readonly IMongoDatabase _database;

    public UserProfileEraser(IMongoDatabase database)
    {
        _database = database;
    }

    public string Name => "user-profile";

    public int Order => UserErasureOrder.Account;

    public Task EraseAsync(UserErasureContext context, CancellationToken cancellationToken = default) =>
        _database
            .GetCollection<BsonDocument>(MongoCollections.Users)
            .DeleteOneAsync(Builders<BsonDocument>.Filter.Eq("_id", context.UserId), cancellationToken);
}
