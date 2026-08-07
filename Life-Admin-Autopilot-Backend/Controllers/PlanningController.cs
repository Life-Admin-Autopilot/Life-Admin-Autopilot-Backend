using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.BLL.Services;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using System.Reflection.Metadata;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class PlanningController : ControllerBase
    {

        private readonly ICommitService _commitService;
        private readonly IUserTaskService _userTaskService;
        private readonly IPlanningOrchestratorService _planningOrchestratorService;
        public PlanningController(
            ICommitService commitService,
            IUserTaskService userTaskService,
            IPlanningOrchestratorService planningOrchestratorService)
        {
            _commitService = commitService;
            _userTaskService = userTaskService;
            _planningOrchestratorService = planningOrchestratorService;
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
                var result = await _commitService.CommitTaskAndDocumentAsync(request, CurrentUserId);

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


        [HttpPost("transcript")]
        public async Task<IActionResult> Transcript([FromBody] TranscriptRequest request)
        {
            var response = await _planningOrchestratorService.ProcessTranscriptAsync(request,AccessToken);

            return Ok(response);
        }

        [HttpPost("clarification")]
        public async Task<IActionResult> Clarification(
            [FromBody] ClarificationRequest request)
        {
            var response = await _planningOrchestratorService.ProcessClarificationAsync(request, CurrentUserId, AccessToken);

            return Ok(response);
        }
        public record DemoRequest(string Transcript, string Mode);
        private string CurrentUserId =>
            User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;

        private string AccessToken =>
            Request.Headers.Authorization.ToString().StartsWith("Bearer ")
            ? Request.Headers.Authorization.ToString()["Bearer ".Length..]
            : string.Empty;
    }

}
