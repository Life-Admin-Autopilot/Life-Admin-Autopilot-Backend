using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.BLL.Features.IcsFeeds;

/// <summary>
/// What an import needs from the user's profile, and nothing else.
/// </summary>
public readonly record struct IcsImportContext(string Timezone, string? DefaultTimeOfDay);

/// <summary>
/// Reads the two profile fields an ICS import depends on.
///
/// <para>
/// A private projection over the <c>users</c> collection rather than a call into the
/// account or auth slice's repository: the read is two fields wide, and taking a
/// dependency on another slice's interface for it would couple two merges together
/// for no benefit.
/// </para>
/// </summary>
public sealed class IcsImportContextReader
{
    private readonly IMongoCollection<UserProfileDocument> _users;

    public IcsImportContextReader(IMongoDatabase database)
    {
        _users = database.GetCollection<UserProfileDocument>(MongoCollections.Users);
    }

    /// <summary>
    /// Returns null when the account has no timezone set.
    ///
    /// <para>
    /// A feed poll runs with NO DEVICE PRESENT, so there is nothing to infer a zone
    /// from and the date-only resolver would throw mid-sync. Callers refuse at the
    /// edge with a message the client can act on instead.
    /// </para>
    /// </summary>
    public async Task<IcsImportContext?> ReadAsync(ObjectId userId, CancellationToken cancellationToken = default)
    {
        var user = await _users
            .Find(Builders<UserProfileDocument>.Filter.Eq(u => u.Id, userId))
            .Project(u => new { u.Timezone, u.Imports })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (user is null || string.IsNullOrEmpty(user.Timezone))
        {
            return null;
        }

        return new IcsImportContext(user.Timezone, user.Imports?.DefaultTimeOfDay);
    }

    /// <summary>The batch form the poller needs — one lookup for a whole tick.</summary>
    public async Task<IReadOnlyDictionary<ObjectId, IcsImportContext>> ReadManyAsync(
        IReadOnlyCollection<ObjectId> userIds,
        CancellationToken cancellationToken = default)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<ObjectId, IcsImportContext>();
        }

        var users = await _users
            .Find(Builders<UserProfileDocument>.Filter.In(u => u.Id, userIds))
            .Project(u => new { u.Id, u.Timezone, u.Imports })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return users
            .Where(u => !string.IsNullOrEmpty(u.Timezone))
            .ToDictionary(u => u.Id, u => new IcsImportContext(u.Timezone!, u.Imports?.DefaultTimeOfDay));
    }
}
