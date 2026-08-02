using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using MongoDB.Bson;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class CommitService : ICommitService
    {
        private readonly IUserTaskRepository _taskRepository;
        private readonly IDocumentRepository _documentRepository;
        private readonly IEmbeddingService _embeddingService;

        public CommitService(
            IUserTaskRepository taskRepository, IDocumentRepository documentRepository,
            IEmbeddingService embeddingService)
        {
            _taskRepository = taskRepository;
            _documentRepository = documentRepository;
            _embeddingService = embeddingService;
        }
        public async Task<CommitTaskResponse> CommitTaskAndDocumentAsync(CommitTaskRequest request)
        {
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
                await _embeddingService.EmbedAsync(userTask, document);

                return new CommitTaskResponse { Task = userTask, Document = document != null ? new Document
                {
                    Id = document.Id,
                    TaskId = document.TaskId,
                    UserId = document.UserId,
                    BlobUrl = document.BlobUrl,
                    ExtractedFields = request.Document.ExtractedFields.HasValue
                        ? BsonDocument.Parse(request.Document.ExtractedFields.Value.GetRawText())
                        : null,
                    Category = document.Category,
                    SourceType = document.SourceType,
                    UploadedAt = document.UploadedAt,
                    ExpiryDate = document.ExpiryDate
                } : null
                };
            }
            catch (Exception ex)
            {
                // COMPENSATING ACTION: If document creation fails after the task was created, 
                // roll back by deleting the orphaned task to maintain data integrity.
                if (userTask != null && userTask.Id != null)
                {
                    await _taskRepository.DeleteAsync(userTask.Id);
                }
                throw; // Re-throw exception so the controller catches it and returns 500
            }
        }
    }
}
