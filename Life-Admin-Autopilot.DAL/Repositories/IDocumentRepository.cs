using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public interface IDocumentRepository
    {
        Task<Document> CreateAsync(Document document);

        Task<Document?> GetByIdAsync(string id);

        Task<List<Document>> GetAllByUserIdAsync(string userId);

        Task<bool> UpdateAsync(
            string id,
            Document document);

        Task<bool> DeleteAsync(string id);
    }
}
