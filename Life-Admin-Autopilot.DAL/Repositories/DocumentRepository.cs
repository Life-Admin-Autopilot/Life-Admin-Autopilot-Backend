using Life_Admin_Autopilot.DAL.Entities;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public class DocumentRepository: IDocumentRepository
    {
        private readonly IMongoCollection<Document> _documents;

        public DocumentRepository(IMongoDatabase database)
        {
            _documents = database.GetCollection<Document>(
                "documents");
        }

        public async Task<Document> CreateAsync(
            Document document)
        {
            await _documents.InsertOneAsync(document);

            return document;
        }

        public async Task<Document?> GetByIdAsync(
            string id)
        {
            return await _documents
                .Find(document => document.Id == id)
                .FirstOrDefaultAsync();
        }

        public async Task<List<Document>> GetAllByUserIdAsync(string userId)
        {
            return await _documents
                .Find(document => document.UserId == userId)
                .ToListAsync();
        }

        public async Task<bool> UpdateAsync(
            string id,
            Document document)
        {
            document.Id = id;

            var result = await _documents.ReplaceOneAsync(
                document => document.Id == id,
                document);

            return result.ModifiedCount > 0;
        }

        public async Task<bool> DeleteAsync(string id)
        {
            var result = await _documents.DeleteOneAsync(
                document => document.Id == id);

            return result.DeletedCount > 0;
        }
    }
}
