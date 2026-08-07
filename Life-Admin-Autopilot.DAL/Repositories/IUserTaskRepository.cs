using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public interface IUserTaskRepository
    {
        Task<UserTask> CreateAsync(UserTask task);

        Task<UserTask?> GetByIdAsync(string id, string userId);

        Task<List<UserTask>> GetAllByUserIdAsync(string userId);

        Task<bool> UpdateAsync(string id, UserTask task, string userId);

        Task<bool> DeleteAsync(string id, string userId);

        Task<List<UserTask>> GetDraftTasksByIdsAsync(IEnumerable<string> ids, string userId);
    }
}
