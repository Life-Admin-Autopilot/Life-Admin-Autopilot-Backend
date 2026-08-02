using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using System.Reflection.Metadata;
using System.Text.Json;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PlanningController : ControllerBase
    {

        private readonly ICommitService _commitService;
        public PlanningController(
            ICommitService commitService)
        {
            _commitService = commitService;
        }

        [HttpPost("commit")]
        public async Task<IActionResult> Commit(
            [FromBody] CommitTaskRequest request)
        {
            if (request?.Task == null)
            {
                return BadRequest(new { Success = false, Message = "Task payload is required." });
            }

            try
            {
                var result = await _commitService.CommitTaskAndDocumentAsync(request);

                return Ok(new
                {
                    task = result.Task,
                    document = result.Document != null ? new DocumentResponse
                    {
                        Id = result.Document.Id,
                        TaskId = result.Document.TaskId,
                        UserId = result.Document.UserId,
                        BlobUrl = result.Document.BlobUrl,
                        ExtractedFields = result.Document.ExtractedFields is not null
                        ? JsonSerializer.Deserialize<JsonElement>(result.Document.ExtractedFields.ToJson())
                        : null,
                        Category = result.Document.Category,
                        SourceType = result.Document.SourceType,
                        UploadedAt = result.Document.UploadedAt,
                        ExpiryDate = result.Document.ExpiryDate
                    } : null
                });
            }
            catch(Exception ex)
            {
                return StatusCode(500, new { Success = false, Message = $"Failed to commit task and document: {ex.Message}" });
            }
        }
    }
}
