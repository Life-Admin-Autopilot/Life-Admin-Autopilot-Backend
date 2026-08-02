using Life_Admin_Autopilot.DAL.Entities;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public class ContentChunksRepository : IContentChunksRepository
    {
        private readonly IMongoCollection<ContentChunks> _contentChunks;

        public ContentChunksRepository(IMongoDatabase database)
        {
            _contentChunks = database.GetCollection<ContentChunks>(
                "contentChunks");
        }
        public async Task<ContentChunks> CreateAsync(ContentChunks contentChunks)
        {
            await _contentChunks.InsertOneAsync(contentChunks);

            return contentChunks;
        }
    }
}
