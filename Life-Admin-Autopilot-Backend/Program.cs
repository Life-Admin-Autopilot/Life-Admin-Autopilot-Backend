using Life_Admin_Autopilot.BLL;
using Life_Admin_Autopilot.DAL;
using Life_Admin_Autopilot.DAL.Extensions;
using Life_Admin_Autopilot.DAL.Push;
using Life_Admin_Autopilot_Backend.Extensions;
using Life_Admin_Autopilot_Backend.Kernel;

namespace Life_Admin_Autopilot_Backend
{
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateApp(args).Run();
        }

        /// <summary>
        /// Builds the configured app.
        ///
        /// <para>
        /// <b>Slice agents must not edit this method.</b> Register services and
        /// endpoints from an <c>IEndpointModule</c> instead — the kernel scans for
        /// them. See <c>docs/KERNEL.md</c>.
        /// </para>
        /// </summary>
        public static WebApplication CreateApp(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            builder.Services.AddDataAccessLayer(builder.Configuration);
            builder.Services.AddBusinessLogicLayer(builder.Configuration);
            builder.Services.AddJwtAuthentication(builder.Configuration);
            builder.Services.AddMongoDb(builder.Configuration);
            builder.Services.AddClaudeService(builder.Configuration);
            builder.Services.AddPushNotifications(builder.Configuration);
            builder.Services.AddSpeechServices(builder.Configuration);
            builder.Services.AddAuthorization();

            builder.Services.AddControllers();

            // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
            builder.Services.AddOpenApi(options =>
            {
                options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
                options.AddOperationTransformer<BearerSecurityRequirementTransformer>();
            });

            // The shared kernel, plus every discovered feature module. Must come
            // after AddMongoDb — it builds on IMongoDatabase.
            builder.Services.AddKernel(builder.Configuration);

            var app = builder.Build();

            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
                app.UseSwaggerUI(options =>
                {
                    options.SwaggerEndpoint("/openapi/v1.json", "Life Admin Autopilot API v1");
                });
            }

            // Off by default: the Node server is plain HTTP and a 307 to https breaks
            // every parity check. Opt in per environment with Kernel:UseHttpsRedirection.
            if (app.Configuration.GetValue("Kernel:UseHttpsRedirection", false))
            {
                app.UseHttpsRedirection();
            }

            // Installs error handling, CORS, auth and the module endpoints — including
            // GET /health.
            app.UseKernel();

            app.MapControllers();

            WarnIfPushIsUnconfigured(app);

            return app;
        }

        /// <summary>
        /// Say out loud, once, at boot, when this deployment cannot send push.
        ///
        /// <para>
        /// Every other symptom of a missing FCM credential is silent. The reminder tick
        /// still claims its entries and still writes its notification rows, so it reports
        /// a healthy run; the only trace is one warning per device per send, buried in a
        /// log nobody reads when nothing appears to be wrong. A total delivery blackout
        /// and a quiet week look identical from the outside — which is exactly how one
        /// went unnoticed.
        /// </para>
        ///
        /// <para>
        /// Not fatal. A dev box without a service account is a legitimate, supported
        /// setup: the app falls back to scheduling reminders on the device itself
        /// (DeviceRegistrationResponse), so everything still works, just locally.
        /// </para>
        /// </summary>
        private static void WarnIfPushIsUnconfigured(WebApplication app)
        {
            var push = app.Services.GetRequiredService<IPushNotificationService>();
            if (push.IsConfigured)
            {
                return;
            }

            app.Services
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Push")
                .LogWarning(
                    "PUSH IS NOT CONFIGURED - no FCM service account, so no notification will ever reach a phone "
                    + "from this server. Clients fall back to scheduling reminders locally. "
                    + "Set FCM_SERVICE_ACCOUNT_FILE or FCM_SERVICE_ACCOUNT_JSON to enable delivery.");
        }
    }
}
