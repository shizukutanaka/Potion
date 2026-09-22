using System.Globalization;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Localization;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Polly;
using Potion.Service.Hubs;
using Potion.Service.Infrastructure;
using Potion.Service.Options;
using Potion.Service.Remediation;

namespace Potion.Service;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    public void ConfigureServices(IServiceCollection services)
    {
        // OpenTelemetry observability (Phase 1 enhancement)
        services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("Potion.Service")
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation()
                    .AddPrometheusExporter()
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(
                            Configuration["Observability:OtlpEndpoint"] ?? "http://localhost:4317");
                    });
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("Potion.Service")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter();
            });

        // Register activity source for custom tracing
        services.AddSingleton(PotionActivitySource.Source);

        // Polly resilience pipelines (Phase 1 enhancement)
        services.AddSingleton<ResiliencePipeline<ProcessResult>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Startup>>();
            return ResiliencePipelines.CreateRemediationPipeline(logger);
        });

        services.AddSingleton<ResiliencePipeline<bool>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Startup>>();
            return ResiliencePipelines.CreateHealthCheckPipeline(logger);
        });

        services.AddSingleton<ResiliencePipeline<DiagnosticReport>>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Startup>>();
            return ResiliencePipelines.CreateDiagnosticPipeline(logger);
        });

        services.AddSignalR();
        services.AddHttpClient();
        services.AddSingleton<CollaborationService>();
        services.AddOptions<CollaborationOptions>();

        // Self-healing monitoring loop: observation and reporting only.
        // Repair-execution services (AutoRecoveryManager, PerformanceOptimizer,
        // PredictiveRemediationService, EventDrivenRemediationService) stay
        // unregistered pending explicit approval — they run OS-level repairs.
        services.AddSingleton<ISystemHealthMonitor, SystemHealthMonitor>();
        services.AddHostedService<MemoryMonitor>();
        services.AddHostedService<AnomalyDetector>();
        services.AddHostedService<EventCorrelationService>();
        services.AddHostedService<ComplianceReportService>();
        services.AddOptions<MemoryMonitorOptions>();
        services.AddOptions<PerformanceOptimizerOptions>();
        services.AddOptions<EventCorrelationOptions>();
        services.AddOptions<ComplianceOptions>();

        var supportedCultures = new[]
        {
            new CultureInfo("en"),
            new CultureInfo("ja"),
            new CultureInfo("es"),
            new CultureInfo("fr"),
            new CultureInfo("de"),
            new CultureInfo("ko"),
            new CultureInfo("zh"),
            new CultureInfo("ru"),
            new CultureInfo("ar"),
            new CultureInfo("hi"),
            new CultureInfo("bn"),
            new CultureInfo("ur"),
            new CultureInfo("id"),
            new CultureInfo("it"),
            new CultureInfo("nl"),
            new CultureInfo("pt"),
            new CultureInfo("vi"), // Vietnamese
            new CultureInfo("th"), // Thai
            new CultureInfo("tr"), // Turkish
            new CultureInfo("pl"), // Polish
            new CultureInfo("uk"), // Ukrainian
            new CultureInfo("cs"), // Czech
            new CultureInfo("hu"), // Hungarian
            new CultureInfo("sv"), // Swedish
            new CultureInfo("no"), // Norwegian
            new CultureInfo("da"), // Danish
            new CultureInfo("fi"), // Finnish
            new CultureInfo("el"), // Greek
            new CultureInfo("he"), // Hebrew
            new CultureInfo("fa"), // Persian
            new CultureInfo("ms"), // Malay
            new CultureInfo("tl"), // Tagalog
            new CultureInfo("my"), // Myanmar
            new CultureInfo("km"), // Khmer
            new CultureInfo("lo"), // Lao
            new CultureInfo("mn"), // Mongolian
            new CultureInfo("sw"), // Swahili
            new CultureInfo("af"), // Afrikaans
            new CultureInfo("ca"), // Catalan
            new CultureInfo("eu"), // Basque
            new CultureInfo("gl"), // Galician
            new CultureInfo("cy"), // Welsh
            new CultureInfo("gd"), // Scottish Gaelic
            new CultureInfo("ga"), // Irish
            new CultureInfo("ne"), // Nepali
            new CultureInfo("si"), // Sinhala
            new CultureInfo("ta"), // Tamil
            new CultureInfo("te")  // Telugu
        };
        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.DefaultRequestCulture = new RequestCulture("en");
            options.SupportedCultures = supportedCultures;
            options.SupportedUICultures = supportedCultures;
            options.RequestCultureProviders.Clear();
            options.RequestCultureProviders.Add(new AcceptLanguageHeaderRequestCultureProvider());
        });

        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddMemoryCache();
        services.AddSingleton<InternationalizationService>(sp =>
        {
            var localizer = sp.GetRequiredService<IStringLocalizer<InternationalizationService>>();
            var cache = sp.GetRequiredService<IMemoryCache>();
            return new InternationalizationService(localizer, cache);
        });
        services.AddSingleton<CircuitBreakerService>();
        services.AddSingleton<IConfigurationHotReloadService, ConfigurationHotReloadService>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRequestLocalization();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthorization();

        // Map Prometheus metrics endpoint (OpenTelemetry export)
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<CollaborationHub>("/collaboration");

            // Prometheus metrics endpoint for scraping
            endpoints.MapPrometheusScrapingEndpoint();
        });
    }
}
