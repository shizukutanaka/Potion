using System;
using System.Globalization;
using System.Linq;
using System.Threading;
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
using Potion.Service.Scheduling;

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
        services.Configure<CollaborationOptions>(Configuration.GetSection("Collaboration"));

        // Self-healing monitoring loop: observation and reporting only.
        services.AddSingleton<ISystemHealthMonitor, SystemHealthMonitor>();
        services.AddHostedService<MemoryMonitor>();
        services.AddHostedService<AnomalyDetector>();
        services.AddHostedService<EventCorrelationService>();
        services.AddHostedService<ComplianceReportService>();
        services.AddHealthChecks();
        services.Configure<MemoryMonitorOptions>(Configuration.GetSection(MemoryMonitorOptions.SectionName));
        services.Configure<PerformanceOptimizerOptions>(Configuration.GetSection(PerformanceOptimizerOptions.SectionName));
        services.Configure<EventCorrelationOptions>(Configuration.GetSection("EventCorrelation"));
        services.Configure<ComplianceOptions>(Configuration.GetSection("Compliance"));

        // Repair-execution tier: these services run OS-level repairs
        // autonomously (service restarts, SFC/DISM, performance tuning), so
        // they are wired only when the operator opts in via the
        // "FeatureFlags:RepairExecutionEnabled" flag — disabled by default.
        if (Configuration.GetValue<bool>("FeatureFlags:RepairExecutionEnabled"))
        {
            services.Configure<RemediationPolicyOptions>(Configuration.GetSection("RemediationPolicy"));
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<ICommandValidator, CommandValidator>();
            services.AddSingleton<IRemediationTaskExecutor, RemediationTaskExecutor>();
            services.AddSingleton<RemediationScheduler>();
            services.AddSingleton<IRemediationScheduler>(sp => sp.GetRequiredService<RemediationScheduler>());
            services.AddHostedService(sp => sp.GetRequiredService<RemediationScheduler>());
            services.AddHostedService<AutoRecoveryManager>();
            services.AddHostedService<PerformanceOptimizer>();
            services.AddHostedService<EventDrivenRemediationService>();
            services.AddHostedService<PredictiveRemediationService>();
        }

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
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseRequestLocalization();
        app.UseStaticFiles();
        app.UseRouting();

        // Map Prometheus metrics endpoint (OpenTelemetry export)
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<CollaborationHub>("/collaboration");
            endpoints.MapHealthChecks("/health");

            // Dashboard API: the wwwroot dashboard fetches these routes.
            // All data comes from the registered ISystemHealthMonitor snapshot.
            endpoints.MapGet("/api/health", async (ISystemHealthMonitor monitor, CancellationToken ct) =>
            {
                var snapshot = await monitor.GetCurrentHealthAsync(ct);
                return Results.Ok(new { metrics = snapshot.Metrics, alerts = snapshot.Alerts });
            });
            endpoints.MapGet("/api/health/metrics", async (ISystemHealthMonitor monitor, CancellationToken ct) =>
            {
                var snapshot = await monitor.GetCurrentHealthAsync(ct);
                return Results.Ok(snapshot.Metrics);
            });
            endpoints.MapGet("/api/health/security", async (ISystemHealthMonitor monitor, CancellationToken ct) =>
            {
                var snapshot = await monitor.GetCurrentHealthAsync(ct);
                var s = snapshot.Metrics.Security;
                return Results.Ok(new
                {
                    defenderStatus = s.WindowsDefenderEnabled ? "Enabled" : "Disabled",
                    firewallStatus = s.FirewallEnabled ? "Enabled" : "Disabled",
                    realTimeProtection = s.WindowsDefenderEnabled ? "Active" : "Inactive",
                    securityCount = s.ActiveThreatCount,
                    securityAlerts = snapshot.Alerts.Select(a => new
                    {
                        message = a.Message,
                        severity = a.Severity.ToString(),
                        timestamp = a.Timestamp,
                    }),
                });
            });
            endpoints.MapGet("/api/health/security/summary", async (ISystemHealthMonitor monitor, CancellationToken ct) =>
            {
                var snapshot = await monitor.GetCurrentHealthAsync(ct);
                var s = snapshot.Metrics.Security;
                var score = 100
                    - (s.WindowsDefenderEnabled ? 0 : 30)
                    - (s.FirewallEnabled ? 0 : 30)
                    - Math.Min(s.ActiveThreatCount * 10, 40);
                return Results.Ok(new { securityScore = Math.Max(score, 0) });
            });

            // Prometheus metrics endpoint for scraping
            endpoints.MapPrometheusScrapingEndpoint();
        });
    }
}
