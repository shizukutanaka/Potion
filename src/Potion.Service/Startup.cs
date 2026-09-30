using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
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
        // One knob drives both exporters — an operator setting
        // Observability:OtlpEndpoint expects metrics and traces to reach it;
        // the parameterless exporter overload would silently keep traces on
        // the env-var/localhost default.
        var otlpEndpoint = new Uri(
            Configuration["Observability:OtlpEndpoint"] ?? "http://localhost:4317");
        services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter("Potion.Service")
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddProcessInstrumentation()
                    .AddPrometheusExporter()
                    .AddOtlpExporter(options => options.Endpoint = otlpEndpoint);
            })
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource("Potion.Service")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter(options => options.Endpoint = otlpEndpoint);
            });

        // Register activity source for custom tracing
        services.AddSingleton(PotionActivitySource.Source);

        // Polly resilience pipelines (Phase 1 enhancement)
        // The dashboard compares alert severities as strings ("Critical");
        // serialize enums as names so /api/health responses match the contract.
        services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        services.AddSignalR();
        services.AddHttpClient();
        // ~230KB of text dashboard assets ship uncompressed otherwise; the
        // default provider set (Brotli + Gzip) covers css/js/html/json and
        // correctly skips already-compressed binaries like woff2.
        services.AddResponseCompression();
        services.AddSingleton<CollaborationService>();
        services.AddOptions<CollaborationOptions>()
            .Bind(Configuration.GetSection("Collaboration"))
            .Validate(o => o.MaxConcurrentUsers > 0,
                "Collaboration:MaxConcurrentUsers must be positive (the hub is always mapped).")
            .ValidateOnStart();

        // Self-healing monitoring loop: observation and reporting only.
        services.AddSingleton<ISystemHealthMonitor, SystemHealthMonitor>();
        services.AddHostedService<MemoryMonitor>();
        services.AddHostedService<AnomalyDetector>();
        // Expose the hosted instance for injection (e.g. CollaborationService
        // subscribes to AnomalyDetector.AnomalyDetected).
        services.AddSingleton(sp => sp.GetServices<IHostedService>().OfType<AnomalyDetector>().Single());
        services.AddSingleton<EventCorrelationStats>();
        services.AddSingleton<RemediationExecutionStats>();
        services.AddSingleton<RequestMetricsTracker>();
        services.AddHostedService<EventCorrelationService>();
        services.AddHostedService<ComplianceReportService>();
        services.AddSingleton<SystemReadinessCheck>();
        services.AddHealthChecks()
            .AddCheck<SystemReadinessCheck>("system_ready", tags: new[] { "ready" });
        services.AddRateLimiter(options =>
        {
            // The alertmanager webhook is the only anonymous write endpoint;
            // bound it so a misbehaving poster cannot flood the service.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter("webhook", limiter =>
            {
                limiter.PermitLimit = 60;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });
        services.AddOptions<MemoryMonitorOptions>()
            .Bind(Configuration.GetSection(MemoryMonitorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<PerformanceOptimizerOptions>()
            .Bind(Configuration.GetSection(PerformanceOptimizerOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<EventCorrelationOptions>()
            .Bind(Configuration.GetSection("EventCorrelation"))
            .Validate(o => !o.Enabled || o.CorrelationWindowMinutes > 0,
                "EventCorrelation:CorrelationWindowMinutes must be positive when enabled.")
            .Validate(o => !o.Enabled || o.MaxEventsToCorrelate > 0,
                "EventCorrelation:MaxEventsToCorrelate must be positive when enabled.")
            .ValidateOnStart();
        services.AddOptions<ComplianceOptions>()
            .Bind(Configuration.GetSection("Compliance"))
            .Validate(o => !o.Enabled || o.ReportIntervalHours is >= 1 and <= 1193,
                "Compliance:ReportIntervalHours must be 1-1193 hours when enabled.")
            .ValidateOnStart();

        // Repair-execution tier: these services run OS-level repairs
        // autonomously (service restarts, SFC/DISM, performance tuning), so
        // they are wired only when the operator opts in via the
        // "FeatureFlags:RepairExecutionEnabled" flag — disabled by default.
        if (Configuration.GetValue<bool>("FeatureFlags:RepairExecutionEnabled"))
        {
            services.AddOptions<RemediationPolicyOptions>()
                .Bind(Configuration.GetSection("RemediationPolicy"))
                .ValidateDataAnnotations()
                .Validate(RemediationPolicyOptionsValidators.HasUniqueTaskNames, "Remediation policy contains duplicate task names.")
                .Validate(RemediationPolicyOptionsValidators.CommandsAreAllowlisted, "Remediation policy references commands outside the allowlist.")
                .Validate(RemediationPolicyOptionsValidators.ArgumentsAreSafe, "Remediation policy contains unsafe task arguments.")
                .Validate(RemediationPolicyOptionsValidators.ArgumentsAreAllowlisted, "Remediation policy uses arguments outside the command argument allowlist.")
                .Validate(RemediationPolicyOptionsValidators.MaintenanceWindowsAreValid, "Remediation policy contains invalid maintenance windows.")
                .Validate(RemediationPolicyOptionsValidators.MaintenanceWindowReferencesAreValid, "Remediation policy tasks reference undefined maintenance window tags.")
                .ValidateOnStart();
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<ICommandValidator, CommandValidator>();
            services.AddSingleton(sp => ResiliencePipelines.CreateProcessExecutionPipeline(
                sp.GetRequiredService<ILogger<RemediationTaskExecutor>>()));
            services.AddSingleton<IRemediationTaskExecutor, RemediationTaskExecutor>();
            services.AddSingleton<RemediationScheduler>();
            services.AddSingleton<IRemediationScheduler>(sp => sp.GetRequiredService<RemediationScheduler>());
            services.AddHostedService(sp => sp.GetRequiredService<RemediationScheduler>());
            services.AddHostedService<AutoRecoveryManager>();
            services.AddHostedService<PerformanceOptimizer>();
            services.AddHostedService<EventDrivenRemediationService>();
            services.AddHostedService<PredictiveRemediationService>();
        }

    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        // Force PotionMetrics' static constructor to run at startup: its
        // ObservableGauge/Counter instruments only exist after first access,
        // so without this the potion.* series never reach the /metrics export.
        _ = Infrastructure.PotionMetrics.SystemHealthScore;

        // Consistent 500 contract: without an exception handler, unhandled
        // endpoint failures surface as Kestrel's bare empty-body 500.
        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Potion.UnhandledException");
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    title = "Internal Server Error",
                    status = 500,
                    traceId = context.TraceIdentifier,
                });
            });
        });

        // Browser-facing dashboard hardening with an enforced CSP: inline script
        // handlers were migrated to data-action delegation in dashboard.js, so
        // script-src no longer needs 'unsafe-inline'.
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] =
                "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            // Fully strict CSP: inline handlers became data-action delegation and
            // inline style attributes became utility classes, so no 'unsafe-inline'
            // is needed for either script-src or style-src.
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; " +
                "style-src 'self'; font-src 'self'; " +
                "img-src 'self' data:; connect-src 'self' ws: wss:; " +
                "object-src 'none'; base-uri 'self'; frame-ancestors 'none'";
            if (context.Request.IsHttps)
            {
                context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000";
            }
            await next();
        });

        // Compression must precede static-file/API responses to wrap them;
        // EnableForHttps stays off (default) to avoid the compression+
        // reflected-secret BREACH consideration on the HTTPS endpoint.
        app.UseResponseCompression();

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
            {
                // All URLs are stable (no content hashes), so nothing may be
                // marked immutable — an upgraded vendored font or stylesheet at
                // the same path must not stay cached. no-cache still allows
                // caching but forces ETag revalidation, so unchanged assets
                // revalidate cheap (304) and upgraded ones are picked up.
                ctx.Context.Response.Headers.CacheControl = "no-cache";
            }
        });
        app.UseMiddleware<RequestMetricsMiddleware>();
        app.UseRouting();
        app.UseRateLimiter();

        // Map Prometheus metrics endpoint (OpenTelemetry export)
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<CollaborationHub>("/collaboration");
            // /health = pure liveness (process responsive); /health/ready runs the
            // 'ready'-tagged check that proves metric sampling works end-to-end.
            endpoints.MapHealthChecks("/health", new HealthCheckOptions { Predicate = check => !check.Tags.Contains("ready") });
            endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

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

            // Alertmanager webhook receiver — monitoring/alertmanager.yml posts here.
            endpoints.MapPost("/api/health/alerts/webhook", async (HttpContext ctx, ILoggerFactory loggerFactory, CancellationToken ct) =>
            {
                var logger = loggerFactory.CreateLogger("Potion.Alerts.Webhook");
                var received = 0;

                JsonDocument document;
                try
                {
                    document = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct);
                }
                catch (JsonException ex)
                {
                    logger.LogWarning("Rejected malformed alertmanager webhook payload: {Error}", ex.Message);
                    return Results.BadRequest(new { error = "malformed JSON payload" });
                }

                using (document)
                {
                    if (document.RootElement.TryGetProperty("alerts", out var alerts)
                        && alerts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var alert in alerts.EnumerateArray())
                        {
                            if (alert.ValueKind != JsonValueKind.Object)
                            {
                                continue;
                            }

                            var status = alert.TryGetProperty("status", out var s) ? s.GetString() : "unknown";
                            var name = alert.TryGetProperty("labels", out var l)
                                && l.ValueKind == JsonValueKind.Object
                                && l.TryGetProperty("alertname", out var an) ? an.GetString() : "unknown";
                            var summary = alert.TryGetProperty("annotations", out var a)
                                && a.ValueKind == JsonValueKind.Object
                                && a.TryGetProperty("summary", out var sum) ? sum.GetString() : null;

                            if (status == "resolved")
                            {
                                logger.LogInformation("Alert resolved: {AlertName} - {Summary}", name, summary);
                            }
                            else
                            {
                                logger.LogWarning("Alert firing: {AlertName} - {Summary}", name, summary);
                            }

                            received++;
                        }
                    }
                }

                return Results.Ok(new { received });
            }).RequireRateLimiting("webhook");

            // Prometheus metrics endpoint for scraping
            endpoints.MapPrometheusScrapingEndpoint();
        });
    }
}
