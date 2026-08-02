using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using System.Text.Json;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PlanningController : ControllerBase
    {
        private readonly IUserTaskRepository _taskRepository;
        private readonly IDocumentRepository _documentRepository;

        public PlanningController(
            IUserTaskRepository taskRepository, IDocumentRepository documentRepository)
        {
            _taskRepository = taskRepository;
            _documentRepository = documentRepository;
        }

        [HttpPost("commit")]
        public async Task<IActionResult> Create(
            [FromBody] CommitTaskRequest request)
        {
            if (request?.Task == null)
            {
                return BadRequest(new { Success = false, Message = "Task payload is required." });
            }

            UserTask? userTask = null;
            Document? document = null;
            try
            {
                userTask = new UserTask
                {
                    UserId = request.Task.UserId,
                    Title = request.Task.Title,
                    DueDate = request.Task.DueDate,
                    Category = request.Task.Category,
                    Priority = request.Task.Priority,
                    SourceType = request.Task.SourceType,
                    Status = "Pending"
                };
                // Commit task to database
                await _taskRepository.CreateAsync(userTask);

                var generatedTaskId = userTask.Id;

                // If a document is involved, create the document record using the valid TaskId
                if (request.Document != null)
                {
                    document = new Document
                    {
                        TaskId = generatedTaskId,
                        UserId = request.Task.UserId,
                        BlobUrl = request.Document.BlobUrl,
                        ExtractedFields = request.Document.ExtractedFields.HasValue
                        ? BsonDocument.Parse(request.Document.ExtractedFields.Value.GetRawText()) : null,
                        SourceType = request.Document.SourceType,
                        UploadedAt = request.Document.UploadedAt,
                        Category = request.Task.Category,
                        ExpiryDate = request.Document.ExpiryDate,
                    };
                    await _documentRepository.CreateAsync(document);
                }

                // Generate embeddings for task and document


                return Ok(new
                {
                    task = userTask,
                    document = document != null ? new DocumentResponse
                    {
                        Id = document.Id,
                        TaskId = document.TaskId,
                        UserId = document.UserId,
                        BlobUrl = document.BlobUrl,

                        ExtractedFields = document.ExtractedFields is not null
                ? JsonSerializer.Deserialize<JsonElement>(
                    document.ExtractedFields.ToJson())
                : null,

                        Category = document.Category,
                        SourceType = document.SourceType,
                        UploadedAt = document.UploadedAt,
                        ExpiryDate = document.ExpiryDate
                    } : null
                });
            }
            catch (Exception ex)
            {
                // COMPENSATING ACTION: If document creation or embedding fails after the task was created, 
                // roll back by deleting the orphaned task to maintain data integrity.
                if (userTask != null && userTask.Id != null)
                {
                    await _taskRepository.DeleteAsync(userTask.Id);
                    if (document != null && document.Id != null)
                    {
                        await _documentRepository.DeleteAsync(document.Id);
                    }
                }

                return StatusCode(500, new { Success = false, Message = $"Failed to commit task and document: {ex.Message}" });
            }
             
          

            
            
        }
    }
}
