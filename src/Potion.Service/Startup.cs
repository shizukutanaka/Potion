using Microsoft.Extensions.Caching.Memory;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

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
        services.AddControllers();
        services.AddEndpointsApiExplorer();

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

        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Potion Self-Healing Service API",
                Version = "v1",
                Description = "Comprehensive health monitoring and system observability API with advanced features including machine learning, blockchain audit trails, and chaos engineering capabilities.",
                Contact = new OpenApiContact
                {
                    Name = "Potion Service Team",
                    Email = "potion-service@example.com"
                }
            });

            // Add security definitions
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                In = ParameterLocation.Header,
                Description = "JWT Authorization header using the Bearer scheme",
                Name = "Authorization",
                Type = SecuritySchemeType.ApiKey,
                BearerFormat = "JWT"
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    new string[] {}
                }
            });

            // Include XML comments
            var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (File.Exists(xmlPath))
            {
                c.IncludeXmlComments(xmlPath);
            }
        });

        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new Microsoft.AspNetCore.Mvc.ApiVersion(1, 0);
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ReportApiVersions = true;
        });

        services.AddSignalR();

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
        services.AddSingleton<ISelfHealingCollectionsService, SelfHealingCollectionsService>();
        services.AddSingleton<IPerformanceOptimizationService, PerformanceOptimizationService>();
        services.AddSingleton<IReactiveEventSystem, ReactiveEventSystem>();
        services.AddSingleton<IFunctionalErrorHandlingService, FunctionalErrorHandlingService>();
        services.AddSingleton<IObservabilityService, ObservabilityService>();
        services.AddSingleton<IMetricsCollectionService, MetricsCollectionService>();
        services.AddSingleton<IConfigurationHotReloadService, ConfigurationHotReloadService>();
        services.AddSingleton<IFeatureFlagService, FeatureFlagService>();
        services.AddSingleton<IChaosEngineeringService, ChaosEngineeringService>();
        services.AddSingleton<IServiceMeshService, ServiceMeshService>();
        services.AddSingleton<IAnomalyDetectionService, AnomalyDetectionService>();
        services.AddSingleton<IAuditTrailService, AuditTrailService>();
        services.AddSingleton<IKubernetesOperatorService, KubernetesOperatorService>();
        services.AddSingleton<IKubernetesHealthService, KubernetesHealthService>();
        services.AddSingleton<IGitOpsService, GitOpsService>();
        services.AddSingleton<IIacService, IacService>();
        services.AddSingleton<IPerformanceAnalyticsService, PerformanceAnalyticsService>();
    }

    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "Potion Self-Healing Service API v1");
                c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.None);
                c.DefaultModelsExpandDepth(-1);
            });
        }

        app.UseRequestLocalization();
        app.UseRouting();
        app.UseAuthorization();

        // Map Prometheus metrics endpoint (OpenTelemetry export)
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapControllers();
            endpoints.MapHub<CollaborationHub>("/collaboration");

            // Prometheus metrics endpoint for scraping
            endpoints.MapPrometheusScrapingEndpoint();
        });
    }
}
