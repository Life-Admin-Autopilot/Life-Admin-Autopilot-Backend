using Life_Admin_Autopilot.DAL.Common;
using Life_Admin_Autopilot.DAL.Embeddings;
using Life_Admin_Autopilot.DAL.Entities;
using Life_Admin_Autopilot.DAL.Repositories;
using Life_Admin_Autopilot.DAL.Storage;
using Life_Admin_Autopilot.DAL.Storage.Models;

namespace Life_Admin_Autopilot.Tests.TestDoubles
{
    // Hand-written rather than mocked, matching the rest of the suite: these record what
    // the service did so a test can assert on it without a mocking framework.
    public class InMemoryUserTaskRepository : IUserTaskRepository
    {
        public List<UserTask> Saved { get; } = [];

        public Task<UserTask> CreateAsync(UserTask task)
        {
            task.Id = "6a757a93de4d379daf3cf4a2";
            Saved.Add(task);

            return Task.FromResult(task);
        }

        public Task<UserTask?> GetByIdAsync(string id) =>
            Task.FromResult(Saved.FirstOrDefault(t => t.Id == id));

        public Task<List<UserTask>> GetAllByUserIdAsync(string userId) =>
            Task.FromResult(Saved.Where(t => t.UserId == userId).ToList());

        public Task<bool> UpdateAsync(string id, UserTask task) => Task.FromResult(true);

        public Task<bool> DeleteAsync(string id) => Task.FromResult(true);
    }

    public class InMemoryDocumentRepository : IDocumentRepository
    {
        public List<Document> Saved { get; } = [];

        public Task<Document> CreateAsync(Document document)
        {
            document.Id = "6a62d93e880eed59f729d61e";
            Saved.Add(document);

            return Task.FromResult(document);
        }

        public Task<Document?> GetByIdAsync(string id) =>
            Task.FromResult(Saved.FirstOrDefault(d => d.Id == id));

        public Task<List<Document>> GetAllByUserIdAsync(string userId) =>
            Task.FromResult(Saved.Where(d => d.UserId == userId).ToList());

        public Task<bool> UpdateAsync(string id, Document document) => Task.FromResult(true);

        public Task<bool> DeleteAsync(string id) => Task.FromResult(true);
    }

    public class InMemoryContentChunkRepository : IContentChunkRepository
    {
        public List<ContentChunk> Saved { get; } = [];

        public Task<ContentChunk> CreateAsync(ContentChunk chunk)
        {
            Saved.Add(chunk);

            return Task.FromResult(chunk);
        }

        public Task<List<ContentChunk>> GetAllByUserIdAsync(string userId) =>
            Task.FromResult(Saved.Where(c => c.UserId == userId).ToList());

        public Task<long> DeleteBySourceAsync(string sourceType, string sourceId) =>
            Task.FromResult(0L);
    }

    public class StubEmbeddingService : IEmbeddingService
    {
        private readonly bool _fails;

        public StubEmbeddingService(bool fails = false) => _fails = fails;

        public string ModelId => "test-model";

        public Task<Result<float[]>> EmbedAsync(string text, CancellationToken cancellationToken = default)
        {
            if (_fails)
            {
                return Task.FromResult(Result<float[]>.Failure(
                    new Error(EmbeddingErrorCodes.RateLimited, "Rate limited.")));
            }

            return Task.FromResult(Result<float[]>.Success(new float[1024]));
        }
    }

    public class StubFileStorageService : IFileStorageService
    {
        private readonly bool _fails;

        public StubFileStorageService(bool fails = false) => _fails = fails;

        public string? Promoted { get; private set; }

        public Task<Result<StoredFile>> PromoteStagedDocumentAsync(
            string stagedPath,
            CancellationToken cancellationToken = default)
        {
            Promoted = stagedPath;

            if (_fails)
            {
                return Task.FromResult(Result<StoredFile>.Failure(
                    new Error(StorageErrorCodes.NotFound, "The staged blob is gone.")));
            }

            return Task.FromResult(Result<StoredFile>.Success(new StoredFile
            {
                Path = stagedPath.Replace("documents-staging/", "documents/"),
                ContentType = "application/pdf",
                SizeBytes = 42
            }));
        }

        public Task<Result<StoredFile>> UploadStagedDocumentAsync(
            string userId, FileUpload file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<StoredFile>> UploadAvatarAsync(
            string userId, FileUpload file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<DownloadedFile>> DownloadAsync(
            string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Result<string> CreateReadUrl(string path, string requestingUserId) =>
            throw new NotSupportedException();

        public Task<Result<bool>> DeleteAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
