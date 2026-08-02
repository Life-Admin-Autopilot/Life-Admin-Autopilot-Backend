using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class EmbeddingService : IEmbeddingService
    {
        private readonly IEmbeddingProvider _embeddingProvider;
        private readonly IContentChunksRepository _contentChunksRepository;
        public EmbeddingService(IEmbeddingProvider embeddingProvider, IContentChunksRepository contentChunksRepository)
        {
            _embeddingProvider = embeddingProvider;
            _contentChunksRepository = contentChunksRepository;
        }
        public async Task EmbedAsync(UserTask task, Document? document = null)
        {
            //  Format and generate vector for task
            var taskText = $"Task: {task.Title}. Category: {task.Category}. Due: {task.DueDate:yyyy-MM-dd}. Priority: {task.Priority}. Status: {task.Status}";
            var taskEmbeddings = await _embeddingProvider.GenerateEmbeddingAsync(taskText);
            var taskContentChunk = new ContentChunks
            {
                UserId = task.UserId,
                SourceType = ChunkSourceType.task,
                SourceId = task.Id,
                Text = taskText,
                Embedding = taskEmbeddings
            };

            await _contentChunksRepository.CreateAsync(taskContentChunk);

            // case document is given
            if (document != null)
            {
                //  Format and generate vector for document 
                var documentText = BuildDocumentText(task,document);

                var documentEmbeddings = await _embeddingProvider.GenerateEmbeddingAsync(documentText);
                var documentContentChunk = new ContentChunks
                {
                    UserId = task.UserId,
                    SourceType = ChunkSourceType.document,
                    SourceId = document.Id,
                    Text = documentText,
                    Embedding = documentEmbeddings
                };

                await _contentChunksRepository.CreateAsync(documentContentChunk);
            }
        }
        private static string BuildDocumentText(UserTask task, Document document)
        {
            var builder = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(document.Category))
                builder.AppendLine($"{document.Category} document.");
            else
                builder.AppendLine("Document.");

            builder.AppendLine($"Related task: {task.Title}.");
            builder.AppendLine($"Document type: {document.SourceType}.");

            if (document.ExtractedFields != null)
            {
                foreach (var field in document.ExtractedFields.Elements)
                {
                    builder.AppendLine($"{ToDisplayName(field.Name)}: {field.Value}");
                }
            }

            if (document.ExpiryDate.HasValue)
            {
                builder.AppendLine($"Expiry date: {document.ExpiryDate.Value:yyyy-MM-dd}.");
            }

            return builder.ToString();
        }

        private static string ToDisplayName(string name)
        {
            return Regex.Replace(name, "(\\B[A-Z])", " $1");
        }
    }
}
