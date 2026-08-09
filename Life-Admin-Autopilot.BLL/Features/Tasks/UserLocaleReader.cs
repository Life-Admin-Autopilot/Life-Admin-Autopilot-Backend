using Life_Admin_Autopilot.DAL.Kernel.Documents;
using Life_Admin_Autopilot.DAL.Kernel.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Features.Tasks;

/// <summary>
/// Port of <c>server/src/modules/ai/userLocale.ts</c>.
///
/// <para>
/// Read from the ACCOUNT rather than taken from the request, even on the
/// interactive path where the client could just send it: the background writers
/// have no request to read, and one code path that is right everywhere beats two
/// that disagree the day a client forgets to send the field.
/// </para>
/// </summary>
public sealed class UserLocaleReader
{
    private readonly IMongoCollection<UserProfileDocument> _users;
    private readonly ILogger<UserLocaleReader> _logger;

    public UserLocaleReader(IMongoDatabase database, ILogger<UserLocaleReader> logger)
    {
        _users = database.GetCollection<UserProfileDocument>(MongoCollections.Users);
        _logger = logger;
    }

    /// <summary>
    /// Degrades to English rather than propagating: a generation that answers in
    /// the wrong language beats one that does not answer at all.
    /// </summary>
    public async Task<string> ReadAsync(ObjectId userId, CancellationToken cancellationToken = default)
    {
        try
        {
            var user = await _users
                .Find(Builders<UserProfileDocument>.Filter.Eq(u => u.Id, userId))
                .Project(u => u.Locale)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            return AiLocales.Resolve(user);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ai:locale-read-failed userId={UserId}", userId);
            return AiLocales.Default;
        }
    }
}
