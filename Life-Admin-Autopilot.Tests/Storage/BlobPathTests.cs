using Life_Admin_Autopilot.DAL.Storage;

namespace Life_Admin_Autopilot.Tests.Storage
{
    public class BlobPathTests
    {
        private const string UserId = "6a1f0c74-0f0e-4f5a-9a52-2f1b0e6f4a11";

        [Fact]
        public void CreatePutsTheUserIdFirstSoOwnershipIsProvableFromThePath()
        {
            var blobName = BlobPath.Create(UserId, "passport.pdf");

            Assert.StartsWith(UserId + "/", blobName);
            Assert.True(BlobPath.IsOwnedBy(blobName, UserId));
        }

        [Fact]
        public void CreateKeepsTheExtensionButDiscardsTheOriginalName()
        {
            var blobName = BlobPath.Create(UserId, "my holiday passport scan.pdf");

            Assert.EndsWith(".pdf", blobName);
            // The stored name is a guid: a user-supplied filename in a blob path is a
            // path-traversal and information-leak risk that is simply avoided.
            Assert.DoesNotContain("holiday", blobName);
        }

        [Fact]
        public void CreateProducesADistinctNameEachTime()
        {
            var first = BlobPath.Create(UserId, "receipt.png");
            var second = BlobPath.Create(UserId, "receipt.png");

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void TrySplitSeparatesContainerFromBlobName()
        {
            var split = BlobPath.TrySplit($"documents/{UserId}/abc.pdf", out var container, out var blobName);

            Assert.True(split);
            Assert.Equal("documents", container);
            Assert.Equal($"{UserId}/abc.pdf", blobName);
        }

        // These come out of the database and off the wire, so malformed input must be a
        // false return rather than an exception.
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("documents")]
        [InlineData("/leading-slash.pdf")]
        [InlineData("trailing-slash/")]
        public void TrySplitRejectsMalformedPaths(string? path)
        {
            Assert.False(BlobPath.TrySplit(path, out _, out _));
        }

        // A traversal attempt must never reach the storage client.
        [Theory]
        [InlineData("documents/../avatars/someone-else.png")]
        [InlineData("documents/user-1/../../secrets.txt")]
        public void TrySplitRejectsPathTraversal(string path)
        {
            Assert.False(BlobPath.TrySplit(path, out _, out _));
        }

        // NFR-5: one user must not be able to read another's document by guessing an id.
        [Fact]
        public void IsOwnedByRejectsAnotherUsersBlob()
        {
            var blobName = BlobPath.Create(UserId, "passport.pdf");

            Assert.False(BlobPath.IsOwnedBy(blobName, "a-different-user"));
        }

        // Guards against a prefix collision letting "user-1" read "user-12"'s files.
        [Fact]
        public void IsOwnedByRequiresAFullSegmentMatchNotAPrefix()
        {
            Assert.False(BlobPath.IsOwnedBy("user-12/abc.pdf", "user-1"));
        }

        [Fact]
        public void CombineRoundTripsThroughTrySplit()
        {
            var blobName = BlobPath.Create(UserId, "receipt.png");
            var path = BlobPath.Combine("documents-staging", blobName);

            Assert.True(BlobPath.TrySplit(path, out var container, out var roundTripped));
            Assert.Equal("documents-staging", container);
            Assert.Equal(blobName, roundTripped);
        }
    }
}
