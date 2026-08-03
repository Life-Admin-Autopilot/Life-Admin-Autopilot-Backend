using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UserTasksController : ControllerBase
    {
        private readonly IUserTaskService _taskService;

        public UserTasksController(
            IUserTaskService taskService)
        {
            _taskService = taskService;
        }

        [HttpPost]
        public async Task<IActionResult> Create(
            TaskPayload request)
        {
            var result = await _taskService.CreateAsync(request);
            return Ok(result);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(
            string id)
        {
            var result = await _taskService.GetByIdAsync(id,CurrentUserId);

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
            var result = await _taskService.GetAllByUserIdAsync(userId);

            return Ok(result);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(
            string id,
            UserTask task)
        {
            var updated = await _taskService.UpdateAsync(id, task,CurrentUserId);

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
            var deleted = await _taskService.DeleteAsync(id,CurrentUserId);

            if (!deleted)
            {
                return NotFound("Cannot delete task.");
            }

            return NoContent();
        }
        private string CurrentUserId =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
    }
}
