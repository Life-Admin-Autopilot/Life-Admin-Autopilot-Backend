using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.GoogleIntegration;

/// <param name="Timezone">
/// Null or empty is a hard stop, never a UTC guess. An import runs with no device
/// present, and guessing UTC for a user in Cairo moves every reminder two hours
/// invisibly.
/// </param>
public readonly record struct GoogleImportProfile(string? Timezone, string? DefaultTimeOfDay);

/// <summary>
/// The two user fields an import needs — Node's
/// <c>User.findById(auth.sub).select('timezone imports')</c>.
///
/// <para>
/// Reads the kernel's own <c>users</c> collection directly rather than borrowing
/// another slice's repository interface, so this slice compiles on its own branch.
/// No mapper is duplicated: nothing here reaches the wire.
/// </para>
/// </summary>
public interface IGoogleImportProfileReader
{
    Task<GoogleImportProfile?> FindAsync(ObjectId userId, CancellationToken cancellationToken = default);

    /// <summary>The poller's batch form, keyed by user id.</summary>
    Task<Dictionary<ObjectId, GoogleImportProfile>> FindManyAsync(
        IReadOnlyCollection<ObjectId> userIds,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGoogleImportProfileReader"/>
public sealed class GoogleImportProfileReader : IGoogleImportProfileReader
{
    private readonly IMongoCollection<UserProfileDocument> _users;

    public GoogleImportProfileReader(IMongoDatabase database)
    {
        _users = database.GetCollection<UserProfileDocument>(MongoCollections.Users);
    }

    public async Task<GoogleImportProfile?> FindAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default)
    {
        var user = await _users
            .Find(Builders<UserProfileDocument>.Filter.Eq(u => u.Id, userId))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return user is null ? null : new GoogleImportProfile(user.Timezone, user.Imports.DefaultTimeOfDay);
    }

    public async Task<Dictionary<ObjectId, GoogleImportProfile>> FindManyAsync(
        IReadOnlyCollection<ObjectId> userIds,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<ObjectId, GoogleImportProfile>();
        }

        var users = await _users
            .Find(Builders<UserProfileDocument>.Filter.In(u => u.Id, userIds))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return users.ToDictionary(
            u => u.Id,
            u => new GoogleImportProfile(u.Timezone, u.Imports.DefaultTimeOfDay));
    }
}
