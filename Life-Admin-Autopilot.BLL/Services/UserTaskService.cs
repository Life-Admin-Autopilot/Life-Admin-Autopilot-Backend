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
        public async Task<UserTask> CreateAsync(TaskPayload request, string userId)
        {
            var userTask = new UserTask
            {
                UserId = userId,
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
        public async Task<bool> UpdateAsync(string id, TaskPayload task, string userId)
        {
            var existingTask = await _taskRepository.GetByIdAsync(id,userId);
            if (existingTask == null)
            {
                return false;
            }

            existingTask.Title = task.Title;
            existingTask.DueDate = task.DueDate;
            existingTask.Status = task.Status;
            existingTask.Category = task.Category;
            existingTask.Priority = task.Priority;
            existingTask.SourceType = task.SourceType;
            return await _taskRepository.UpdateAsync(id, existingTask, userId);
        }
        public async Task<bool> DeleteAsync(string id, string userId)
        {
            return await _taskRepository.DeleteAsync(id, userId);
        }
        public async Task<List<UserTask>> GetDraftTasksByIdsAsync(IEnumerable<string> ids,string userId)
        {
            return await _taskRepository.GetDraftTasksByIdsAsync(ids,userId);
        }
    }
}
