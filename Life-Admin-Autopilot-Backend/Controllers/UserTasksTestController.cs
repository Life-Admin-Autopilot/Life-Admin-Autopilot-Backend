using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UserTasksTestController : ControllerBase
    {
        private readonly IUserTaskRepository _taskRepository;

        public UserTasksTestController(
            IUserTaskRepository taskRepository)
        {
            _taskRepository = taskRepository;
        }

        [HttpPost]
        public async Task<IActionResult> Create(
            UserTask task)
        {
            var result = await _taskRepository.CreateAsync(task);

            return Ok(result);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(
            string id)
        {
            var result = await _taskRepository.GetByIdAsync(id);

            if (result is null)
            {
                return NotFound("Task not found.");
            }

            return Ok(result);
        }

        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetAllByUserId(
            string userId)
        {
            var result = await _taskRepository.GetAllByUserIdAsync(userId);

            return Ok(result);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(
            string id,
            UserTask task)
        {
            var updated = await _taskRepository.UpdateAsync(id, task);

            if (!updated)
            {
                return NotFound("Cannot update task.");
            }

            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(
            string id)
        {
            var deleted = await _taskRepository.DeleteAsync(id);

            if (!deleted)
            {
                return NotFound("Cannot delete task.");
            }

            return NoContent();
        }
    }
}
