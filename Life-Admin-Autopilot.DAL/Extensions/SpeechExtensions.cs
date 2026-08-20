using Life_Admin_Autopilot.DAL.Configurations;
using Life_Admin_Autopilot.DAL.Speech;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;

namespace Life_Admin_Autopilot.DAL.Extensions
{
    public static class SpeechExtensions
    {
        public static IServiceCollection AddSpeechServices(
                this IServiceCollection services,
                IConfiguration configuration)
        {
            services
                .AddOptions<SpeechOptions>()
                .Bind(configuration.GetSection(SpeechOptions.SectionName))
                .PostConfigure(options =>
                {
                    // The Hugging Face token comes from HF_TOKEN (an env var in real
                    // deployments, user-secrets locally) - never from appsettings.json.
                    options.ApiKey = configuration["HF_TOKEN"] ?? string.Empty;
                });

            services
                .AddOptions<AzureSpeechOptions>()
                .Bind(configuration.GetSection(AzureSpeechOptions.SectionName))
                .PostConfigure(options =>
                {
                    // Same rule, same reason: a flat root key, never appsettings.json.
                    options.ApiKey = configuration["AZURE_SPEECH_KEY"] ?? string.Empty;

                    // The endpoint is not a secret, but it is per-resource, so it belongs
                    // beside the key rather than in a checked-in file. Accepted as a flat
                    // key so both halves live on adjacent lines of one .env, while
                    // Speech:Azure:Endpoint still works for anyone configuring it the
                    // structured way.
                    var endpoint = configuration["AZURE_SPEECH_ENDPOINT"];
                    if (!string.IsNullOrWhiteSpace(endpoint))
                    {
                        options.Endpoint = endpoint;
                    }
                });

            // Bound from the same section as the options above, so the HTTP client and the
            // error messages read the same numbers. This used to be
            // `configuration.GetValue("Speech:TimeoutSeconds", 30)` sitting beside a
            // `SpeechOptions.TimeoutSeconds = 30` that fed only an error message: two
            // hardcoded defaults for one setting, free to drift, with the class-level one
            // quietly not the real timeout. A missing section keeps the class defaults.
            var speech = configuration.GetSection(SpeechOptions.SectionName).Get<SpeechOptions>()
                ?? new SpeechOptions();
            var azure = configuration.GetSection(AzureSpeechOptions.SectionName).Get<AzureSpeechOptions>()
                ?? new AzureSpeechOptions();

            // Typed clients on the CONCRETE providers, not on ITranscriptionService: each
            // keeps its own timeout and its own retry policy, and the interface is answered
            // by the wrapper below.
            services
                .AddHttpClient<NemotronTranscriptionService>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(speech.TimeoutSeconds);
                })
                // Linear, short backoff rather than the exponential one used for Claude and
                // FCM: a user is waiting on this call, so recovering slowly is no better
                // than failing fast and telling them to try again.
                .AddTransientHttpErrorPolicy(policyBuilder =>
                    policyBuilder.WaitAndRetryAsync(
                        speech.MaxRetryAttempts,
                        retryAttempt => TimeSpan.FromMilliseconds(250 * retryAttempt)));

            services
                .AddHttpClient<AzureFastTranscriptionService>(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(azure.TimeoutSeconds);
                })
                .AddTransientHttpErrorPolicy(policyBuilder =>
                    policyBuilder.WaitAndRetryAsync(
                        azure.MaxRetryAttempts,
                        retryAttempt => TimeSpan.FromMilliseconds(250 * retryAttempt)));

            // Singleton: which providers are currently dry has to outlive a request. The
            // WRAPPER stays transient - AddHttpClient registers transient typed clients and
            // a singleton holding one would pin its HttpMessageHandler past the pool's
            // rotation.
            services.AddSingleton<ProviderHealth>();

            // Built by hand rather than by constructor injection: both transports are
            // ITranscriptionService, so the container could not tell them apart, and the
            // wrapper needs them NAMED and ORDERED. Naming them here also keeps the wrapper
            // ignorant of which concrete transport is which, which is what makes it
            // testable against stubs.
            services.AddTransient<ITranscriptionService>(serviceProvider =>
                new FailoverTranscriptionService(
                    FailoverTranscriptionService.InPreferenceOrder(
                        speech.PrimaryProvider,
                        new TranscriptionProvider(
                            FailoverTranscriptionService.Nemotron,
                            serviceProvider.GetRequiredService<NemotronTranscriptionService>()),
                        new TranscriptionProvider(
                            FailoverTranscriptionService.Azure,
                            serviceProvider.GetRequiredService<AzureFastTranscriptionService>())),
                    serviceProvider.GetRequiredService<ProviderHealth>(),
                    serviceProvider.GetRequiredService<IOptions<SpeechOptions>>(),
                    serviceProvider.GetRequiredService<ILogger<FailoverTranscriptionService>>()));

            return services;
        }
    }
}
