using System;
using System.Text;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SW.Bus.RabbitMqViewer;
using SW.CqApi;
using SW.PrimitiveTypes;

namespace SW.Bus.SampleWeb
{
    public class AppSettings
    {
        public string BackgroundProcess { get; set; } = "all";
    }

    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services)
        {
            services.AddMemoryCache();
            services.AddControllers().AddJsonOptions(config =>
            {
                config.JsonSerializerOptions.PropertyNamingPolicy = null;
            });

            // ── Bus core ───────────────────────────────────────────────────────
            services.AddBus(config =>
            {
                config.ApplicationName   = "SampleApp";
                config.Token.Key         = Configuration["Token:Key"];
                config.Token.Issuer      = Configuration["Token:Issuer"];
                config.Token.Audience    = Configuration["Token:Audience"];

                // Monitoring cache — 5 s default is fine for the sample
                config.MonitoringCacheSeconds = 5;

                // Operational event pipeline tuning
                config.OperationalEventsEnabled         = true;
                config.OperationalEventsStoreCapacity   = 10000;
                config.OperationalEventsFlushIntervalMs = 500;  // faster flush for demo

                // Alert thresholds
                config.AlertRetryWarningThreshold       = 5;
                config.AlertRetryCriticalThreshold      = 20;
                config.AlertDeadLetterCriticalThreshold = 10;
                config.QueueBackpressureThreshold       = 500;

                // ── Per-queue overrides ────────────────────────────────────────
                // Fast, high-throughput consumers
                config.AddQueueOption("OrderConsumer.OrderCreatedMessage",           prefetch: 8);
                config.AddQueueOption("EmailNotificationConsumer.EmailNotificationMessage", prefetch: 16);
                config.AddQueueOption("PushNotificationConsumer.PushNotificationMessage",  prefetch: 20);

                // Medium consumers
                config.AddQueueOption("DataSyncConsumer.DataSyncMessage",            prefetch: 4);
                config.AddQueueOption("OrderShippedConsumer.OrderShippedMessage",    prefetch: 6);
                config.AddQueueOption("OrderCancellationConsumer.OrderCancelledMessage",
                    prefetch: 4, retryCount: 3, retryAfterSeconds: 10);

                // Slow consumer — prefetch 1 to avoid parallelism
                config.AddQueueOption("ReportingConsumer.DailyReportMessage",        prefetch: 1);

                // Noisy/unreliable consumers — quick retries for demo visibility
                config.AddQueueOption("BuggyConsumer.PersonDto",
                    prefetch: 4, retryCount: 3, retryAfterSeconds: 5);
                config.AddQueueOption("PaymentFailedConsumer.PaymentFailedMessage",
                    prefetch: 4, retryCount: 4, retryAfterSeconds: 8);
                config.AddQueueOption("SmsNotificationConsumer.SmsNotificationMessage",
                    prefetch: 8, retryCount: 3, retryAfterSeconds: 5);
            });

            services.AddCqApi();

            // ── Operations Viewer ──────────────────────────────────────────────
            // Uses built-in Basic auth — credentials come from appsettings.json:
            //   BusViewer:Username / BusViewer:Password  (default keys)
            // Resolved at request-time so secrets can rotate without restart.
            services.AddBusViewer(o =>
            {
                o.UseBasicAuth();   // username: admin / password: admin (see appsettings.json)
                o.Title = "SampleApp — Bus Operations";
                // Allow anonymous in production for this sample app.
                // Production guard is off because this is a local dev sample,
                // not a real production deployment.
                o.AllowAnonymousInProduction = true;
            });

            services.AddScoped<RequestContext>();

            var appSettings = new AppSettings();
            Configuration.Bind(nameof(AppSettings), appSettings);

            services.AddBusPublish();

            switch (appSettings.BackgroundProcess?.ToLower())
            {
                case "all":
                    services.AddBusConsume();
                    break;
                case "broadcast":
                    services.AddBusListen();
                    break;
            }

            // ── Background sample publisher — keeps dashboard alive ─────────
            services.AddHostedService<SamplePublisherBackgroundService>();

            // ── JWT / Cookie auth ─────────────────────────────────────────────
            services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                .AddCookie(options =>
                {
                    options.LoginPath        = "/login";
                    options.AccessDeniedPath = "/";
                })
                .AddJwtBearer(options =>
                {
                    options.RequireHttpsMetadata = false;
                    options.SaveToken            = true;
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer      = Configuration["Token:Issuer"],
                        ValidAudience    = Configuration["Token:Audience"],
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(Configuration["Token:Key"] ?? "dev-key-not-set"))
                    };
                });
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> startupLogger)
        {
            if (env.IsDevelopment())
                app.UseDeveloperExceptionPage();

            // Print dashboard URL on startup so it's impossible to miss
            startupLogger.LogInformation("╔══════════════════════════════════════════════════╗");
            startupLogger.LogInformation("║  SW.Bus Operations Dashboard                     ║");
            startupLogger.LogInformation("║  http://localhost:5000/bus-viewer                ║");
            startupLogger.LogInformation("║  Username: admin   Password: admin               ║");
            startupLogger.LogInformation("╚══════════════════════════════════════════════════╝");

            // No HTTPS redirect — sample app serves on HTTP only.
            // Static files before routing so /_content/SimplyWorks.Bus.RabbitMqViewer/
            // assets are served by UseStaticFiles without being intercepted by the router.
            app.UseStaticFiles();

            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();

            // Marks intent — Bus viewer Razor Pages are auto-discovered by MapRazorPages()
            app.UseBusViewer();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapRazorPages();   // discovers both host pages and BusViewer area
            });
        }
    }
}
