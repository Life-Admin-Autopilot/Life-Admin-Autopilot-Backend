using Life_Admin_Autopilot.DAL.Kernel.Errors;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Kernel.Mongo;

/// <summary>
/// Base for every Mongo-backed kernel/slice repository.
///
/// <para>
/// Its whole reason to exist is <see cref="NotDeleted"/> and
/// <see cref="VisibleOpen"/>. Node spreads <c>notDeleted()</c> across 40+ call
/// sites and <c>visibleOpen()</c> across every clarification surface; a single
/// forgotten spread resurrects trashed rows or makes two screens disagree about
/// the same number. Composing them here means a slice cannot forget.
/// </para>
///
/// <para>
/// <b>Rule for slices:</b> never hand-write <c>deletedAt</c> or the
/// <c>status/deferredUntil</c> pair. Always start from
/// <c>UserScoped(userId)</c>, <c>NotDeleted()</c> or <c>VisibleOpen(now)</c> and
/// <c>Builders&lt;T&gt;.Filter.And(...)</c> onto it.
/// </para>
/// </summary>
public abstract class MongoRepositoryBase<TDocument>
{
    protected MongoRepositoryBase(IMongoDatabase database, string collectionName)
    {
        Database = database;
        Collection = database.GetCollection<TDocument>(collectionName);
    }

    protected IMongoDatabase Database { get; }

    public IMongoCollection<TDocument> Collection { get; }

    protected static FilterDefinitionBuilder<TDocument> Filter => Builders<TDocument>.Filter;

    protected static UpdateDefinitionBuilder<TDocument> Update => Builders<TDocument>.Update;

    protected static SortDefinitionBuilder<TDocument> Sort => Builders<TDocument>.Sort;

    /// <summary>
    /// Port of <c>models/Task.ts</c> <c>notDeleted()</c> —
    /// <c>{ deletedAt: { $exists: false } }</c>.
    ///
    /// Note it is <c>$exists</c>, NOT <c>{ deletedAt: null }</c>. Mongoose omits
    /// unset optional fields entirely, so an <c>$eq: null</c> test would also match
    /// a row explicitly stored as null and, more importantly, cannot use the same
    /// index. Keep the operator identical.
    /// </summary>
    public static FilterDefinition<TDocument> NotDeleted() =>
        Filter.Exists("deletedAt", false);

    /// <summary>
    /// Port of <c>models/Clarification.ts</c> <c>visibleOpen(now)</c> — the one
    /// definition of "a question the user should currently see":
    /// <c>{ status: 'open', $or: [ { deferredUntil: { $exists: false } }, { deferredUntil: { $lte: now } } ] }</c>.
    ///
    /// Every surface that counts or lists held items MUST compose this. The
    /// dashboard once derived its number from a 50-capped page while the digest
    /// aggregated the whole collection, and the two disagreed for anyone with a
    /// real backlog.
    /// </summary>
    public static FilterDefinition<TDocument> VisibleOpen(DateTime now) =>
        Filter.And(
            Filter.Eq("status", "open"),
            Filter.Or(
                Filter.Exists("deferredUntil", false),
                Filter.Lte("deferredUntil", now)));

    /// <summary>Scope to one owner. Always the first clause of any user-facing read.</summary>
    public static FilterDefinition<TDocument> UserScoped(ObjectId userId) =>
        Filter.Eq("userId", userId);

    /// <summary>
    /// <c>UserScoped(userId) AND NotDeleted()</c> — the default read scope for any
    /// soft-deletable collection.
    /// </summary>
    public static FilterDefinition<TDocument> LiveForUser(ObjectId userId) =>
        Filter.And(UserScoped(userId), NotDeleted());

    /// <summary>
    /// Parse a route/body value that must be an ObjectId. A malformed value throws
    /// <see cref="ObjectIdCastException"/>, which the error middleware turns into
    /// <c>404 not_found</c> — mirroring Mongoose's CastError behaviour rather than
    /// letting each handler hand-validate.
    /// </summary>
    public static ObjectId ParseObjectId(string? value)
    {
        if (!ObjectId.TryParse(value, out var parsed))
        {
            throw new ObjectIdCastException(value ?? string.Empty);
        }

        return parsed;
    }
}
