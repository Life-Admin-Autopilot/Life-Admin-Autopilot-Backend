using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public interface IUserTaskRepository
    {
        Task<UserTask> CreateAsync(UserTask task);

        Task<UserTask?> GetByIdAsync(string id);

        Task<List<UserTask>> GetAllByUserIdAsync(string userId);

        Task<bool> UpdateAsync(string id, UserTask task);

        Task<bool> DeleteAsync(string id);
    }
}
