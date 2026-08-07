using Life_Admin_Autopilot.BLL.Dtos;
using Life_Admin_Autopilot.BLL.Services;
using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Embeddings;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Life_Admin_Autopilot.DAL.Storage;
using Life_Admin_Autopilot.DAL.Storage.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;

namespace Life_Admin_Autopilot.Tests.Planning
{
    public class PlanningServiceTests
    {
        private const string UserId = "6a1f0c74-0f0e-4f5a-9a52-2f1b0e6f4a11";

        [Fact]
        public async Task RefusesATaskWithNoDueDate()
        {
            var tasks = new InMemoryUserTaskRepository();
            var service = Build(tasks);

            var response = await service.CommitAsync(UserId, Request(withDueDate: false));

            Assert.False(response.Succeeded);
            Assert.Equal("COMMIT_NO_DUE_DATE", response.ErrorCode);
            // A refused commit must not leave a half-saved task behind.
            Assert.Empty(tasks.Saved);
        }

        [Fact]
        public async Task RefusesATaskWithNoTitle()
        {
            var tasks = new InMemoryUserTaskRepository();
            var service = Build(tasks);

            var response = await service.CommitAsync(UserId, Request(title: "  "));

            Assert.Equal("COMMIT_NO_TITLE", response.ErrorCode);
            Assert.Empty(tasks.Saved);
        }

        // NFR-5: the body carries a userId because the agent sends one, but the owner has
        // to come from the token or a caller could write into someone else's list.
        [Fact]
        public async Task IgnoresTheUserIdInTheBody()
        {
            var tasks = new InMemoryUserTaskRepository();
            var service = Build(tasks);

            var request = Request();
            request.Task.UserId = "somebody-else";

            await service.CommitAsync(UserId, request);

            Assert.Equal(UserId, Assert.Single(tasks.Saved).UserId);
        }

        [Fact]
        public async Task KeepsCategoryAndPriority()
        {
            var tasks = new InMemoryUserTaskRepository();
            var service = Build(tasks);

            await service.CommitAsync(UserId, Request());

            var saved = Assert.Single(tasks.Saved);
            Assert.Equal("Vehicle", saved.Category);
            Assert.Equal("urgent", saved.Priority);
        }

        [Theory]
        [InlineData("draft", "pending")]
        [InlineData("string", "pending")]
        [InlineData(null, "pending")]
        // "overdue" is derived from the due date passing, so it must never be stored as a
        // status a caller chose.
        [InlineData("overdue", "pending")]
        [InlineData("Completed", "completed")]
        public async Task NormalisesStatusToTheAgreedSet(string? given, string expected)
        {
            var tasks = new InMemoryUserTaskRepository();
            var service = Build(tasks);

            await service.CommitAsync(UserId, Request(status: given));

            Assert.Equal(expected, Assert.Single(tasks.Saved).Status);
        }

        [Fact]
        public async Task IndexesTheSavedTaskForSearch()
        {
            var chunks = new InMemoryContentChunkRepository();
            var service = Build(new InMemoryUserTaskRepository(), chunks: chunks);

            var response = await service.CommitAsync(UserId, Request());

            Assert.True(response.Indexed);
            var chunk = Assert.Single(chunks.Saved);
            Assert.Equal("task", chunk.SourceType);
            Assert.Equal(1024, chunk.Embedding.Length);
            // Recorded so a later model change is auditable rather than silently
            // degrading search.
            Assert.Equal("test-model", chunk.EmbeddingModel);
            // The chunk repeats the fields a question would mention, not just the title.
            Assert.Contains("Category: Vehicle", chunk.Text);
            Assert.Contains("Priority: urgent", chunk.Text);
        }

        // The user already confirmed the save. A dead embedding provider must not throw
        // that away - it only means the task is not searchable yet.
        [Fact]
        public async Task StillSavesWhenIndexingFails()
        {
            var tasks = new InMemoryUserTaskRepository();
            var chunks = new InMemoryContentChunkRepository();
            var service = Build(tasks, chunks: chunks, embedder: new StubEmbeddingService(fails: true));

            var response = await service.CommitAsync(UserId, Request());

            Assert.True(response.Succeeded);
            Assert.Single(tasks.Saved);
            Assert.False(response.Indexed);
            Assert.Contains("not indexed", response.IndexWarning);
            Assert.Empty(chunks.Saved);
        }

        [Fact]
        public async Task PromotesAStagedDocumentAndLinksItToTheTask()
        {
            var documents = new InMemoryDocumentRepository();
            var storage = new StubFileStorageService();
            var service = Build(new InMemoryUserTaskRepository(), documents: documents, storage: storage);

            var request = Request();
            request.Document = new CommitDocument
            {
                BlobUrl = $"documents-staging/{UserId}/abc.pdf",
                SourceType = "pdf"
            };

            var response = await service.CommitAsync(UserId, request);

            Assert.Equal($"documents-staging/{UserId}/abc.pdf", storage.Promoted);
            var saved = Assert.Single(documents.Saved);
            Assert.Equal($"documents/{UserId}/abc.pdf", saved.BlobUrl);
            Assert.Equal(response.TaskId, saved.TaskId);
            Assert.Equal(DocumentSourceType.pdf, saved.SourceType);
        }

        // A path already in the permanent container is not an error - re-promoting it
        // would be, so it must be left alone.
        [Fact]
        public async Task DoesNotPromoteAPathThatIsAlreadyCommitted()
        {
            var storage = new StubFileStorageService();
            var service = Build(new InMemoryUserTaskRepository(), storage: storage);

            var request = Request();
            request.Document = new CommitDocument { BlobUrl = $"documents/{UserId}/abc.pdf" };

            await service.CommitAsync(UserId, request);

            Assert.Null(storage.Promoted);
        }

        [Fact]
        public async Task KeepsTheTaskWhenPromotingTheDocumentFails()
        {
            var tasks = new InMemoryUserTaskRepository();
            var documents = new InMemoryDocumentRepository();
            var service = Build(tasks, documents: documents,
                storage: new StubFileStorageService(fails: true));

            var request = Request();
            request.Document = new CommitDocument { BlobUrl = $"documents-staging/{UserId}/abc.pdf" };

            var response = await service.CommitAsync(UserId, request);

            Assert.True(response.Succeeded);
            Assert.Single(tasks.Saved);
            Assert.Empty(documents.Saved);
            Assert.Contains("not attached", response.IndexWarning);
        }

        private static CommitRequest Request(
            string title = "Renew the car licence",
            bool withDueDate = true,
            string? status = null) => new()
            {
                Task = new CommitTask
                {
                    Title = title,
                    // A nullable default cannot express "deliberately absent" here, so the
                    // absence is a flag - otherwise `dueDate: null` silently gets a date.
                    DueDate = withDueDate ? new DateTime(2026, 8, 20, 8, 0, 0, DateTimeKind.Utc) : null,
                    Category = "Vehicle",
                    Priority = "urgent",
                    SourceType = "voice",
                    Status = status
                }
            };

        private static PlanningService Build(
            InMemoryUserTaskRepository tasks,
            InMemoryDocumentRepository? documents = null,
            InMemoryContentChunkRepository? chunks = null,
            StubFileStorageService? storage = null,
            StubEmbeddingService? embedder = null) =>
            new(
                tasks,
                documents ?? new InMemoryDocumentRepository(),
                chunks ?? new InMemoryContentChunkRepository(),
                storage ?? new StubFileStorageService(),
                embedder ?? new StubEmbeddingService(),
                new RecordingLogger<PlanningService>());
    }
}
