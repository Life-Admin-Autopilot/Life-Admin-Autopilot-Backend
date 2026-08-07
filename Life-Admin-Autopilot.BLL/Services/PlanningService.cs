using System.Globalization;
using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.DAL.Embeddings;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Life_Admin_Autopilot.DAL.Storage;
using Microsoft.Extensions.Logging;

namespace Life_Admin_Autopilot.BLL.Services
{
    public class PlanningService : IPlanningService
    {
        // The status a newly confirmed task starts in. "overdue" is never stored - it is
        // derived from the due date having passed.
        private const string InitialStatus = "pending";

        private static readonly HashSet<string> AllowedStatuses =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "pending", "in progress", "completed", "cancelled"
            };

        private readonly IUserTaskRepository _taskRepository;
        private readonly IDocumentRepository _documentRepository;
        private readonly IContentChunkRepository _contentChunkRepository;
        private readonly IFileStorageService _fileStorageService;
        private readonly IEmbeddingService _embeddingService;
        private readonly ILogger<PlanningService> _logger;

        public PlanningService(
            IUserTaskRepository taskRepository,
            IDocumentRepository documentRepository,
            IContentChunkRepository contentChunkRepository,
            IFileStorageService fileStorageService,
            IEmbeddingService embeddingService,
            ILogger<PlanningService> logger)
        {
            _taskRepository = taskRepository;
            _documentRepository = documentRepository;
            _contentChunkRepository = contentChunkRepository;
            _fileStorageService = fileStorageService;
            _embeddingService = embeddingService;
            _logger = logger;
        }

        public async Task<CommitResponse> CommitAsync(
            string userId,
            CommitRequest request,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return CommitResponse.Fail("COMMIT_NO_USER", "No authenticated user.");
            }

            var draft = request.Task;

            if (string.IsNullOrWhiteSpace(draft.Title))
            {
                return CommitResponse.Fail("COMMIT_NO_TITLE", "A task needs a title.");
            }

            // A task with no due date can never produce a reminder, which is the point of
            // saving it. The agent is told to ask instead; this is the backstop.
            if (draft.DueDate is null)
            {
                return CommitResponse.Fail(
                    "COMMIT_NO_DUE_DATE",
                    "A task needs a due date before it can be saved.");
            }

            var task = new UserTask
            {
                UserId = userId,
                Title = draft.Title.Trim(),
                DueDate = draft.DueDate,
                Status = NormaliseStatus(draft.Status),
                SourceType = string.IsNullOrWhiteSpace(draft.SourceType) ? "text" : draft.SourceType.Trim(),
                Category = string.IsNullOrWhiteSpace(draft.Category) ? null : draft.Category.Trim(),
                Priority = string.IsNullOrWhiteSpace(draft.Priority) ? null : draft.Priority.Trim()
            };

            var savedTask = await _taskRepository.CreateAsync(task);

            var response = new CommitResponse
            {
                Succeeded = true,
                TaskId = savedTask.Id
            };

            if (request.Document is not null && !string.IsNullOrWhiteSpace(request.Document.BlobUrl))
            {
                await AttachDocumentAsync(userId, savedTask, request.Document, response, cancellationToken);
            }

            await IndexAsync(userId, savedTask, response, cancellationToken);

            return response;
        }

        private async Task AttachDocumentAsync(
            string userId,
            UserTask task,
            CommitDocument draft,
            CommitResponse response,
            CancellationToken cancellationToken)
        {
            var storedPath = draft.BlobUrl.Trim();

            // Only a staged blob needs promoting. A caller re-committing an already
            // permanent path should not be treated as an error.
            if (storedPath.StartsWith("documents-staging/", StringComparison.OrdinalIgnoreCase))
            {
                var promoted = await _fileStorageService.PromoteStagedDocumentAsync(storedPath, cancellationToken);

                if (promoted.IsFailure)
                {
                    // The task is already saved. Losing the attachment is worth saying
                    // out loud, but it does not undo the save.
                    _logger.LogWarning(
                        "Could not promote {Path} for task {TaskId}: {Code} {Message}",
                        storedPath, task.Id, promoted.Error!.Code, promoted.Error.Message);

                    response.IndexWarning =
                        $"The document could not be moved out of staging ({promoted.Error.Code}), so it was not attached.";
                    return;
                }

                storedPath = promoted.Value!.Path;
            }

            var document = new Document
            {
                TaskId = task.Id!,
                UserId = userId,
                BlobUrl = storedPath,
                Category = string.IsNullOrWhiteSpace(draft.Category) ? null : draft.Category.Trim(),
                SourceType = ParseSourceType(draft.SourceType, storedPath),
                UploadedAt = draft.UploadedAt ?? DateTime.UtcNow,
                ExpiryDate = draft.ExpiryDate
            };

            var savedDocument = await _documentRepository.CreateAsync(document);

            response.DocumentId = savedDocument.Id;
            response.DocumentPath = storedPath;
        }

        private async Task IndexAsync(
            string userId,
            UserTask task,
            CommitResponse response,
            CancellationToken cancellationToken)
        {
            var text = BuildChunkText(task);

            var embedding = await _embeddingService.EmbedAsync(text, cancellationToken);

            if (embedding.IsFailure)
            {
                // Deliberately not a failed commit: the task is saved and usable, it just
                // will not turn up in Copilot Chat until it is re-indexed. Saying so beats
                // rolling back a save the user already confirmed.
                _logger.LogWarning(
                    "Task {TaskId} was saved but not indexed: {Code} {Message}",
                    task.Id, embedding.Error!.Code, embedding.Error.Message);

                response.Indexed = false;
                response.IndexWarning =
                    $"Saved, but not indexed for search ({embedding.Error.Code}). Copilot Chat will not find it yet.";
                return;
            }

            await _contentChunkRepository.CreateAsync(new ContentChunk
            {
                UserId = userId,
                SourceType = "task",
                SourceId = task.Id!,
                Text = text,
                Embedding = embedding.Value!,
                EmbeddingModel = _embeddingService.ModelId
            });

            response.Indexed = true;
        }

        // Matches the sentence the existing chunks were built from, so a query and a chunk
        // are phrased alike. Embedding the bare title loses the category and date, which
        // are exactly what "what do I owe this month" needs to match on.
        private static string BuildChunkText(UserTask task)
        {
            var due = task.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "no date";

            return $"Task: {task.Title}. Category: {task.Category ?? "General"}. " +
                   $"Due: {due}. Priority: {task.Priority ?? "normal"}. Status: {task.Status}";
        }

        private static string NormaliseStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return InitialStatus;
            }

            var trimmed = status.Trim();

            // Anything outside the agreed set - including "overdue", which is derived, and
            // the "draft"/"string" values already sitting in Atlas - becomes pending
            // rather than widening the vocabulary further.
            return AllowedStatuses.Contains(trimmed) ? trimmed.ToLowerInvariant() : InitialStatus;
        }

        private static DocumentSourceType ParseSourceType(string? sourceType, string path)
        {
            if (Enum.TryParse<DocumentSourceType>(sourceType, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            return path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                ? DocumentSourceType.pdf
                : DocumentSourceType.photo;
        }
    }
}
