using Azure.Core;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using System.Reflection.Metadata;
using System.Text.Json;
using Document = Life_Admin_Autopilot.DAL.Entities.Document;

namespace Life_Admin_Autopilot_Backend.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DocumentsTestController : ControllerBase
    {
        private readonly IDocumentRepository _documentRepository;

        public DocumentsTestController(
            IDocumentRepository documentRepository)
        {
            _documentRepository = documentRepository;
        }

        [HttpPost]
        public async Task<IActionResult> Create(
            CreateDocument request)
        {
            var document = new Document
            {
                TaskId = request.TaskId,
                UserId = request.UserId,
                BlobUrl = request.BlobUrl,
                ExtractedFields = request.ExtractedFields.HasValue
                ? BsonDocument.Parse(request.ExtractedFields.Value.GetRawText()) : null,
                Category = request.Category,
                SourceType = request.SourceType,
                UploadedAt = request.UploadedAt,
                ExpiryDate = request.ExpiryDate
            };

            var result = await _documentRepository.CreateAsync(document);

            var response = new DocumentResponse
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
            };

            return Ok(response);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(
            string id)
        {
            var document = await _documentRepository.GetByIdAsync(id);

            if (document is null)
            {
                return NotFound("Document not found.");
            }
            var response = new DocumentResponse
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
            };
            return Ok(response);
        }

        [HttpGet("user/{userId}")]
        public async Task<IActionResult> GetAllByUserId(
            string userId)
        {
            var documents = await _documentRepository.GetAllByUserIdAsync(userId);

            var responses = documents.Select(document =>
                new DocumentResponse
                {
                    Id = document.Id,
                    TaskId = document.TaskId,
                    UserId = document.UserId,
                    BlobUrl = document.BlobUrl,

                    ExtractedFields =
                        document.ExtractedFields is not null
                            ? JsonSerializer.Deserialize<JsonElement>(
                                document.ExtractedFields.ToJson())
                            : null,

                    Category = document.Category,
                    SourceType = document.SourceType,
                    UploadedAt = document.UploadedAt,
                    ExpiryDate = document.ExpiryDate
                }).ToList();

            return Ok(responses);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(
            string id,
            UpdateDocument request)
        {
            var document = new Document
            {
                TaskId = request.TaskId,
                UserId = request.UserId,
                BlobUrl = request.BlobUrl,
                ExtractedFields = request.ExtractedFields.HasValue 
                ? BsonDocument.Parse(request.ExtractedFields.Value.GetRawText()) : null,
                Category = request.Category,
                SourceType = request.SourceType,
                UploadedAt = request.UploadedAt,
                ExpiryDate = request.ExpiryDate
            };

            var updated = await _documentRepository.UpdateAsync(id, document);

            if (!updated)
            {
                return NotFound("Cannot update document.");
            }

            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(
            string id)
        {
            var deleted = await _documentRepository.DeleteAsync(id);

            if (!deleted)
            {
                return NotFound("Cannot delete document.");
            }

            return NoContent();
        }
    }
}
