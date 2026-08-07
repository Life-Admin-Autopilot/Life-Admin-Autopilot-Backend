using Life_Admin_Autopilot.DAL.Entities;
using MongoDB.Driver;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public class ContentChunkRepository : IContentChunkRepository
    {
        private readonly IMongoCollection<ContentChunk> _chunks;

        public ContentChunkRepository(IMongoDatabase database)
        {
            _chunks = database.GetCollection<ContentChunk>("contentChunks");
        }

        public async Task<ContentChunk> CreateAsync(ContentChunk chunk)
        {
            await _chunks.InsertOneAsync(chunk);

            return chunk;
        }

        public async Task<List<ContentChunk>> GetAllByUserIdAsync(string userId)
        {
            return await _chunks
                .Find(chunk => chunk.UserId == userId)
                .ToListAsync();
        }

        public async Task<long> DeleteBySourceAsync(string sourceType, string sourceId)
        {
            var result = await _chunks.DeleteManyAsync(
                chunk => chunk.SourceType == sourceType && chunk.SourceId == sourceId);

            return result.DeletedCount;
        }
    }
}
