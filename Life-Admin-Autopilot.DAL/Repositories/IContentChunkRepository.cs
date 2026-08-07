using Life_Admin_Autopilot.DAL.Entities;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public interface IContentChunkRepository
    {
        Task<ContentChunk> CreateAsync(ContentChunk chunk);

        Task<List<ContentChunk>> GetAllByUserIdAsync(string userId);

        // Used when a task or document is deleted, so its vector does not linger and keep
        // surfacing in Copilot Chat answers for something that no longer exists.
        Task<long> DeleteBySourceAsync(string sourceType, string sourceId);
    }
}
