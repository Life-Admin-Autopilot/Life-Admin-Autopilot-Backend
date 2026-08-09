using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Features.Account;

/// <summary>
/// The Account slice's single read against the <c>users</c> collection.
///
/// <para>
/// Deliberately narrow: this slice is read-only and server-managed, so it owns no
/// collection of its own and needs neither an <c>IUserDataEraser</c> nor an
/// <c>IMongoIndexProvider</c> — <c>users</c> belongs to the account/auth domain and
/// is already covered by <c>KernelIndexProvider</c> and the kernel's
/// <c>Account</c>-order eraser.
/// </para>
/// </summary>
public interface IAccountProfileRepository
{
    /// <summary>
    /// The profile behind an access token, or <see langword="null"/> when the row is
    /// gone. A cryptographically valid token outlives the account it names, so the
    /// null case is a routine 404 rather than an error.
    /// </summary>
    Task<UserProfileDocument?> FindByIdAsync(ObjectId userId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAccountProfileRepository"/>
public sealed class AccountProfileRepository : MongoRepositoryBase<UserProfileDocument>, IAccountProfileRepository
{
    public AccountProfileRepository(IMongoDatabase database)
        : base(database, MongoCollections.Users)
    {
    }

    /// <summary>
    /// Mirrors <c>User.findById(auth.sub).lean()</c>.
    ///
    /// <para>
    /// No <c>NotDeleted()</c> here on purpose: Node deletes an account with a real
    /// <c>User.deleteOne</c>, not a soft delete, so there is no <c>deletedAt</c> on
    /// this collection to compose. The kernel's rule applies to the soft-deletable
    /// collections (tasks and friends), not to <c>users</c>.
    /// </para>
    /// </summary>
    public async Task<UserProfileDocument?> FindByIdAsync(
        ObjectId userId,
        CancellationToken cancellationToken = default) =>
        await Collection
            .Find(Filter.Eq(user => user.Id, userId))
            .FirstOrDefaultAsync(cancellationToken);
}
