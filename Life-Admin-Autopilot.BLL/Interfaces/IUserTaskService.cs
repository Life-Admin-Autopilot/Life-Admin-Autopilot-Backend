using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IUserTaskService
    {
        Task<UserTask> CreateAsync(TaskPayload request);

        Task<UserTask?> GetByIdAsync(string id, string userId);

        Task<List<UserTask>> GetAllByUserIdAsync(string userId);

        Task<bool> UpdateAsync(string id, UserTask task, string userId);

        Task<bool> DeleteAsync(string id, string userId);
    }
}
