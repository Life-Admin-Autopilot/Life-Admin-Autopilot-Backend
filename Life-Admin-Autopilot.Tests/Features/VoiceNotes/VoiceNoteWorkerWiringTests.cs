using Life_Admin_Autopilot.BLL.Features.VoiceNotes;
using Life_Admin_Autopilot.Tests.Kernel;
using Life_Admin_Autopilot_Backend.Features.VoiceNotes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Life_Admin_Autopilot.Tests.Features.VoiceNotes;

/// <summary>
/// That the worker's dependencies actually resolve.
///
/// <para>
/// <b>Nothing else can catch this.</b> The worker resolves its collaborators from a
/// scope, lazily, on a background timer — so a missing registration is not a startup
/// failure and not an endpoint failure. It is one line in a log the user never sees,
/// followed by a note that retries four times and settles at <c>failed</c>, and it
/// looks exactly like a provider outage. The whole slice was in that state before
/// this branch: the null transcriber and extractor were registered and nothing ever
/// called <c>services.Replace</c>, so every note had been failing for real reasons
/// nobody could tell apart from configuration.
/// </para>
/// </summary>
public sealed class VoiceNoteWorkerWiringTests : IClassFixture<KernelWebApplicationFactory>
{
    private readonly KernelWebApplicationFactory _factory;

    public VoiceNoteWorkerWiringTests(KernelWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public void every_collaborator_the_worker_asks_for_can_be_built()
    {
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        Assert.NotNull(services.GetRequiredService<IVoiceTranscriber>());
        Assert.NotNull(services.GetRequiredService<IVoiceExtractor>());
        Assert.NotNull(services.GetRequiredService<IVoiceExtractionCommit>());
        Assert.NotNull(services.GetRequiredService<IVoiceClarificationStaging>());

        // The one that is easiest to forget, because it reaches across three slices
        // — notifications for the row, document-scans for the push preference, and
        // the push service itself.
        Assert.NotNull(services.GetRequiredService<VoiceNoteOutcomeNotifier>());
    }

    /// <summary>
    /// Which adapter each configuration selects.
    ///
    /// <para>
    /// <b>Read off the registrations, not out of a running host.</b>
    /// <c>KernelWebApplicationFactory.With</c> does not reach the
    /// <c>IConfiguration</c> that <c>IEndpointModule.AddServices</c> is handed — a
    /// known test-infra quirk the voice endpoint tests already document — so a host
    /// built with these keys silently keeps whatever the developer's own environment
    /// selected, and the assertion would pass or fail for reasons that have nothing
    /// to do with the code. The descriptor is the thing under test anyway: the defect
    /// this branch fixes was that nothing ever called <c>services.Replace</c> at all.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("hf-token", "planning-key", typeof(NemotronVoiceTranscriber), typeof(PlanningVoiceExtractor))]
    [InlineData("hf-token", "", typeof(NemotronVoiceTranscriber), typeof(NullVoiceExtractor))]
    [InlineData("", "planning-key", typeof(NullVoiceTranscriber), typeof(PlanningVoiceExtractor))]

    // The parity target, and it has to remain reachable: a deployment with neither
    // key must behave exactly as it did before the adapters existed — every note
    // failing honestly — rather than half-working.
    [InlineData("", "", typeof(NullVoiceTranscriber), typeof(NullVoiceExtractor))]
    public void each_provider_is_selected_on_the_key_it_actually_needs(
        string speechToken,
        string planningKey,
        Type transcriber,
        Type extractor)
    {
        var services = new ServiceCollection();
        services.AddVoiceNotesFeature(Configuration(speechToken, planningKey));

        Assert.Equal(transcriber, ImplementationOf<IVoiceTranscriber>(services));
        Assert.Equal(extractor, ImplementationOf<IVoiceExtractor>(services));
    }

    private static Type? ImplementationOf<TService>(IServiceCollection services) =>
        services.Last(d => d.ServiceType == typeof(TService)).ImplementationType;

    private static IConfiguration Configuration(string speechToken, string planningKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HF_TOKEN"] = speechToken,
                ["PLANNING_API_KEY"] = planningKey,

                // PlanningOptions falls back to the embeddings credential when the
                // planning one is unset, because in practice they are the same Google
                // key. Pinned here, or an environment that has one would decide the
                // "no planning key" rows for us.
                ["EMBEDDINGS_API_KEY"] = string.Empty,
            })
            .Build();
}
