using Azure;
using Life_Admin_Autopilot.DAL.Kernel.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Life_Admin_Autopilot.Tests.Kernel.Storage;

/// <summary>
/// Round-trips real bytes through a real container.
///
/// <para>
/// These run only when <c>AZURE_STORAGE_CONNECTION_STRING</c> is in the
/// environment, and are inert otherwise — the same arrangement the Mongo-backed
/// suites use, so a clone with no storage account reports a clean run rather
/// than a wall of failures for infrastructure it was never given.
/// </para>
///
/// <para>
/// A fake would prove nothing here. Every edge that matters — whether a missing
/// blob throws or returns empty, whether re-uploading the same key is allowed,
/// whether a delete of nothing is an error — is the SDK's behaviour, not ours,
/// and those are exactly the places the local-disk store had to be matched.
/// </para>
/// </summary>
public sealed class AzureBlobStoreTests
{
    private const string TestContainer = "documents-staging";

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");

    private static bool Configured => !string.IsNullOrWhiteSpace(ConnectionString);

    // A key nothing else will collide with, under a folder that names itself as
    // disposable — these run against the shared dev account.
    private static string NewKey() => $"__tests__/{Guid.NewGuid():N}.bin";

    [SkippableFact]
    public async Task stores_bytes_and_reads_the_same_bytes_back()
    {
        Skip.IfNot(Configured);

        var store = new AzureBlobStore(ConnectionString!, TestContainer);
        var key = NewKey();
        var payload = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x00, 0xFF, 0x10 };

        try
        {
            await store.PutAsync(key, payload);
            var read = await store.GetAsync(key);

            Assert.Equal(payload, read);
        }
        finally
        {
            await store.RemoveAsync(key);
        }
    }

    [SkippableFact]
    public async Task reading_a_missing_blob_throws_rather_than_returning_nothing()
    {
        Skip.IfNot(Configured);

        var store = new AzureBlobStore(ConnectionString!, TestContainer);

        // Matches File.ReadAllBytesAsync. A row pointing at absent bytes is a
        // server fault the routes surface as 500, not a 404 the client can act
        // on — returning an empty array here would turn that into a silent
        // success and a zero-byte download.
        await Assert.ThrowsAsync<RequestFailedException>(() => store.GetAsync(NewKey()));
    }

    [SkippableFact]
    public async Task removing_a_blob_that_is_not_there_is_silent()
    {
        Skip.IfNot(Configured);

        var store = new AzureBlobStore(ConnectionString!, TestContainer);

        // File.Delete does not throw on a missing file, and the user-data eraser
        // walks every stored key it can find — a throw here would abort account
        // deletion partway through, leaving some blobs behind and no row to find
        // them by.
        await store.RemoveAsync(NewKey());
    }

    [SkippableFact]
    public async Task re_uploading_the_same_key_overwrites_instead_of_failing()
    {
        Skip.IfNot(Configured);

        var store = new AzureBlobStore(ConnectionString!, TestContainer);
        var key = NewKey();

        try
        {
            await store.PutAsync(key, new byte[] { 1, 1, 1 });
            await store.PutAsync(key, new byte[] { 2, 2 });

            // A retried scan reuses its key. Without overwrite the second attempt
            // fails on BlobAlreadyExists and the retry ladder can never succeed.
            Assert.Equal(new byte[] { 2, 2 }, await store.GetAsync(key));
        }
        finally
        {
            await store.RemoveAsync(key);
        }
    }

    [SkippableFact]
    public async Task a_key_containing_dot_dot_is_refused()
    {
        Skip.IfNot(Configured);

        var store = new AzureBlobStore(ConnectionString!, TestContainer);

        // Not a traversal defence on blob storage — there is no parent directory
        // to escape into. It is here so a key refused by the disk store is
        // refused by this one too, and swapping backends cannot quietly widen
        // what a crafted key may address.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.PutAsync("../escaped.bin", [0x00]));
    }

    [Fact]
    public void options_stay_unconfigured_when_no_connection_string_is_present()
    {
        var options = AzureBlobOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection([]).Build());

        // The fallback to local disk hangs off this. If an empty configuration
        // ever read as configured, a teammate's clone would try to reach a
        // storage account it has no credentials for, and every upload would fail
        // at the point of use instead of never being attempted.
        Assert.False(options.IsConfigured);
        Assert.Equal("documents", options.DocumentsContainer);
        Assert.Equal("voice-notes", options.VoiceNotesContainer);
    }

    [Fact]
    public void the_env_var_spelling_and_the_colon_spelling_both_configure_it()
    {
        var fromEnvName = AzureBlobOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AZURE_STORAGE_CONNECTION_STRING"] = "UseDevelopmentStorage=true",
                }).Build());

        var fromColonName = AzureBlobOptions.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Azure:Storage:ConnectionString"] = "UseDevelopmentStorage=true",
                }).Build());

        // The secret is already stored under the env-var spelling on the machine
        // this was built on, while .NET configuration idiom is the colon form.
        // Supporting only one silently ignores the other.
        Assert.True(fromEnvName.IsConfigured);
        Assert.True(fromColonName.IsConfigured);
    }
}
