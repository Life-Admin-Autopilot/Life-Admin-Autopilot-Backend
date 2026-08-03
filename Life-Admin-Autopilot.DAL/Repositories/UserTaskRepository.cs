using Life_Admin_Autopilot.DAL.Entities;
using MongoDB.Driver;
using System;
using System.Collections.Generic;
using System.Text;

namespace Life_Admin_Autopilot.DAL.Repositories
{
    public class UserTaskRepository: IUserTaskRepository
    {
        private readonly IMongoCollection<UserTask> _tasks;
        private readonly IMongoCollection<Document> _documents;

        public UserTaskRepository(IMongoDatabase database)
        {
            _tasks = database.GetCollection<UserTask>("tasks");
            _documents = database.GetCollection<Document>(
                "documents");
        }

        public async Task<UserTask> CreateAsync(UserTask task)
        {
            await _tasks.InsertOneAsync(task);

            return task;
        }

        public async Task<UserTask?> GetByIdAsync(string id, string userId)
        {
            return await _tasks
                .Find(task => task.Id == id && task.UserId == userId)
                .FirstOrDefaultAsync();
        }

        public async Task<List<UserTask>> GetAllByUserIdAsync(string userId)
        {
            return await _tasks
                .Find(task => task.UserId == userId)
                .ToListAsync();
        }

        public async Task<bool> UpdateAsync(
            string id,
            UserTask task,
            string userId)
        {
            task.Id = id;

            var result = await _tasks.ReplaceOneAsync(
                task => task.Id == id && task.UserId == userId,
                task);

            return result.ModifiedCount > 0;
        }

        public async Task<bool> DeleteAsync(string id, string userId)
        {
            var result = await _tasks.DeleteOneAsync(
                task => task.Id == id && task.UserId == userId);

            // delete documents related to deleted task
            if(result.DeletedCount > 0)
            {
                await _documents.DeleteManyAsync(
                document => document.TaskId == id);
            }

            return result.DeletedCount > 0;
        }
    }
}
