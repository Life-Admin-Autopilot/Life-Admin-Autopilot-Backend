using Azure.Storage.Blobs;
using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Storage;
using Life_Admin_Autopilot.DAL.Storage.Models;
using Life_Admin_Autopilot.Tests.TestDoubles;
using Microsoft.Extensions.Options;

namespace Life_Admin_Autopilot.Tests.Storage
{
    // These cover every branch that runs before a network call: configuration checks, path
    // validation, ownership, and SAS generation - which is local cryptography, not a
    // request. The parts that do reach Azure are verified live instead.
    public class AzureBlobStorageServiceTests
    {
        private const string UserId = "6a1f0c74-0f0e-4f5a-9a52-2f1b0e6f4a11";

        private const string FakeConnectionString =
            "DefaultEndpointsProtocol=https;AccountName=testaccount;" +
            "AccountKey=dGVzdGtleTEyMzQ1Njc4OTBhYmNkZWZnaGlqa2xtbm9wcXI=;EndpointSuffix=core.windows.net";

        // An unconfigured environment must fail per-call, not at startup - the rest of the
        // API has to keep working.
        [Fact]
        public async Task ReportsNotConfigured_WhenThereIsNoConnectionString()
        {
            var service = CreateService(configured: false);

            var upload = await service.UploadStagedDocumentAsync(UserId, Upload(), CancellationToken.None);
            var download = await service.DownloadAsync("documents/a/b.pdf");
            var readUrl = service.CreateReadUrl("documents/a/b.pdf", UserId);

            Assert.Equal(StorageErrorCodes.NotConfigured, upload.Error!.Code);
            Assert.Equal(StorageErrorCodes.NotConfigured, download.Error!.Code);
            Assert.Equal(StorageErrorCodes.NotConfigured, readUrl.Error!.Code);
        }

        [Fact]
        public async Task RejectsAnEmptyUpload_BeforeCallingAzure()
        {
            var service = CreateService();

            var result = await service.UploadStagedDocumentAsync(
                UserId,
                new FileUpload { FileName = "empty.pdf", ContentType = "application/pdf", LengthBytes = 0 },
                CancellationToken.None);

            Assert.Equal(StorageErrorCodes.NoFile, result.Error!.Code);
        }

        // NFR-5: a read URL is the actual access grant, so ownership is enforced here
        // rather than trusting whatever id the caller passed alongside it.
        [Fact]
        public void RefusesAReadUrlForAnotherUsersFile()
        {
            var service = CreateService();
            var otherUsersPath = $"documents/{Guid.NewGuid()}/secret.pdf";

            var result = service.CreateReadUrl(otherUsersPath, UserId);

            Assert.Equal(StorageErrorCodes.AccessDenied, result.Error!.Code);
        }

        [Fact]
        public void IssuesAReadUrlForTheOwnersOwnFile()
        {
            var service = CreateService();
            var path = BlobPath.Combine("documents", BlobPath.Create(UserId, "passport.pdf"));

            var result = service.CreateReadUrl(path, UserId);

            Assert.True(result.IsSuccess);
            Assert.Contains("testaccount.blob.core.windows.net", result.Value);
            // Signed, time-limited, read-only - not a bare blob URL.
            Assert.Contains("sig=", result.Value);
            Assert.Contains("se=", result.Value);
            Assert.Contains("sp=r", result.Value);
        }

        [Theory]
        [InlineData("not-a-path")]
        [InlineData("documents/../avatars/someone.png")]
        public void RejectsMalformedOrTraversingPaths(string path)
        {
            var service = CreateService();

            Assert.Equal(StorageErrorCodes.NotFound, service.CreateReadUrl(path, UserId).Error!.Code);
        }

        // Promoting from anywhere other than staging would mean a caller has its wires
        // crossed; copying it silently would hide the bug.
        [Fact]
        public async Task RefusesToPromoteABlobThatIsNotStaged()
        {
            var service = CreateService();

            var result = await service.PromoteStagedDocumentAsync($"documents/{UserId}/already-committed.pdf");

            Assert.Equal(StorageErrorCodes.AccessDenied, result.Error!.Code);
            Assert.Contains("documents-staging", result.Error.Message);
        }

        [Fact]
        public async Task LogsEveryFailure()
        {
            var logger = new RecordingLogger<AzureBlobStorageService>();
            var service = new AzureBlobStorageService(
                new BlobClientProvider(null),
                Options.Create(new StorageOptions()),
                logger);

            await service.DownloadAsync("nonsense");

            Assert.Single(logger.Warnings);
        }

        private static FileUpload Upload() => new()
        {
            Content = new MemoryStream([1, 2, 3]),
            FileName = "passport.pdf",
            ContentType = "application/pdf",
            LengthBytes = 3
        };

        private static AzureBlobStorageService CreateService(bool configured = true) =>
            new(
                new BlobClientProvider(configured ? new BlobServiceClient(FakeConnectionString) : null),
                Options.Create(new StorageOptions()),
                new RecordingLogger<AzureBlobStorageService>());
    }
}
