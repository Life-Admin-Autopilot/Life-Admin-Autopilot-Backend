using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Profile;

/// <summary>
/// The profile slice's writes against <c>users</c>.
///
/// <para>
/// Read-only access to the same collection already exists twice (the account
/// slice's <c>IAccountProfileRepository</c> and the auth slice's
/// <c>IUserProfileRepository</c>), so this interface deliberately adds only the
/// one operation neither of them has: the dot-notation patch.
/// </para>
/// </summary>
public interface IProfileRepository
{
    /// <summary>
    /// Mirrors <c>User.findByIdAndUpdate(id, { $set }, { new: true })</c>.
    ///
    /// <para>
    /// <b><paramref name="set"/> holds DOT-NOTATION keys.</b> <c>notifications.push</c>
    /// updates one sub-field and leaves its siblings alone; <c>notifications</c> would
    /// replace the whole sub-document. The route depends on the former — see
    /// <c>UpdateMeSet</c>.
    /// </para>
    /// </summary>
    /// <param name="now">
    /// Written into <c>updatedAt</c> as part of the same <c>$set</c>. NOT optional:
    /// Mongoose's <c>timestamps: true</c> injects <c>updatedAt</c> into the update
    /// document itself, and the .NET driver does not — a line-by-line port leaves
    /// the field stale and every later read diverges.
    /// </param>
    /// <returns>The document AFTER the update, or null when the account is gone.</returns>
    Task<UserProfileDocument?> ApplyPatchAsync(
        ObjectId userId,
        IReadOnlyList<KeyValuePair<string, BsonValue>> set,
        DateTime now,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IProfileRepository"/>
public sealed class ProfileRepository : MongoRepositoryBase<UserProfileDocument>, IProfileRepository
{
    public ProfileRepository(IMongoDatabase database)
        : base(database, MongoCollections.Users)
    {
    }

    public async Task<UserProfileDocument?> ApplyPatchAsync(
        ObjectId userId,
        IReadOnlyList<KeyValuePair<string, BsonValue>> set,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        var fields = new BsonDocument();
        foreach (var (key, value) in set)
        {
            fields[key] = value;
        }

        // Always last, and always present — so an EMPTY patch is still a touch.
        // `PATCH /me {}` returns 200 with a BUMPED updatedAt on the reference,
        // because Mongoose adds the timestamp to a `$set` that would otherwise be
        // empty. Verified live.
        fields["updatedAt"] = now;

        return await Collection
            .FindOneAndUpdateAsync<UserProfileDocument>(
                Filter.Eq(user => user.Id, userId),
                new BsonDocument("$set", fields),
                new FindOneAndUpdateOptions<UserProfileDocument, UserProfileDocument>
                {
                    ReturnDocument = ReturnDocument.After,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
