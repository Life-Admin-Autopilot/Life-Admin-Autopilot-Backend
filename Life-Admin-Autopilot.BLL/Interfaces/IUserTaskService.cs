using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.DAL.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Interfaces
{
    public interface IUserTaskService
    {
        Task<UserTask> CreateAsync(TaskPayload request, string userId);

        Task<UserTask?> GetByIdAsync(string id, string userId);

        Task<List<UserTask>> GetAllByUserIdAsync(string userId);

        Task<bool> UpdateAsync(string id, TaskPayload task, string userId);

        Task<bool> DeleteAsync(string id, string userId);
        Task<List<UserTask>> GetDraftTasksByIdsAsync(IEnumerable<string> ids, string userId);
    }
}
