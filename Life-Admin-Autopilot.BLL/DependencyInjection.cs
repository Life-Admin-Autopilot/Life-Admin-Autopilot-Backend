using Life_Admin_Autopilot.BLL.Interfaces;
using Life_Admin_Autopilot.BLL.Services;
using Life_Admin_Autopilot.BLL.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Life_Admin_Autopilot.BLL
{
    public static class DependencyInjection
    {
        public static IServiceCollection AddBusinessLogicLayer(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<JwtSettings>(configuration.GetSection("Jwt"));

            services.AddScoped<IAuthService, AuthService>();
            services.AddScoped<IJwtTokenService, JwtTokenService>();
            services.AddScoped<INotificationService, NotificationService>();
            services.AddScoped<ISpeechToTextService, SpeechToTextService>();
            services.AddScoped<ICommitService, CommitService>();
            services.AddScoped<IEmbeddingService, EmbeddingService>();
            services.AddScoped<IUserTaskService, UserTaskService>();
            services.AddScoped<ILangflowClientService, LangflowClientService>();
            services.AddScoped<IPlanningOrchestratorService, PlanningOrchestratorService>();

            services
            .AddOptions<HuggingFaceSettings>()
            .Bind(configuration.GetSection(HuggingFaceSettings.SectionName));

            services.AddHttpClient<IEmbeddingProvider, HuggingFaceEmbeddingProvider>();

            services
            .AddOptions<LangflowSettings>()
            .Bind(configuration.GetSection(LangflowSettings.SectionName));

            services.AddHttpClient<ILangflowClientService, LangflowClientService>();

            return services;
        }
    }
}