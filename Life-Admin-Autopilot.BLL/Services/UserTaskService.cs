using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class UserTaskService : IUserTaskService
    {
        private readonly IUserTaskRepository _taskRepository;
        public UserTaskService(IUserTaskRepository taskRepository)
        {
            _taskRepository = taskRepository;
        }
        public async Task<UserTask> CreateAsync(TaskPayload request)
        {
            var userTask = new UserTask
            {
                UserId = request.UserId,
                Title = request.Title,
                DueDate = request.DueDate,
                Category = request.Category,
                Priority = request.Priority,
                SourceType = request.SourceType,
                Status = "Pending"
            };

            return await _taskRepository.CreateAsync(userTask);
            
        }
        public async Task<UserTask?> GetByIdAsync(string id, string userId)
        {
            return await _taskRepository.GetByIdAsync(id, userId);
        }
        public async Task<List<UserTask>> GetAllByUserIdAsync(string userId)
        {
            return await _taskRepository.GetAllByUserIdAsync(userId);
        }
        public async Task<bool> UpdateAsync(string id, UserTask task, string userId)
        {
            return await _taskRepository.UpdateAsync(id, task, userId);
        }
        public async Task<bool> DeleteAsync(string id, string userId)
        {
            return await _taskRepository.DeleteAsync(id, userId);
        }
    }
}
