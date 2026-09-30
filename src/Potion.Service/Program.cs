using Potion.Service;
using Potion.Service.Infrastructure;
using Serilog;
using Serilog.Events;

// A bootstrap logger exists until UseSerilog swaps in the configured one at
// Build(). Without it, a failure inside builder.Build() (DI validation, bad
// JSON, missing sink assembly) hits the silent default logger and a Windows
// service exits leaving no log anywhere — Log.Fatal becomes a no-op.
var bootstrapConfig = new LoggerConfiguration().WriteTo.Console();
if (OperatingSystem.IsWindows())
{
    // Same source as appsettings.Production.json so a start failure surfaces
    // in Event Viewer even when no configured sink ever ran.
    bootstrapConfig = bootstrapConfig.WriteTo.EventLog(
        source: "Potion Self-Healing Service",
        logName: "Application",
        restrictedToMinimumLevel: LogEventLevel.Warning);
}
Log.Logger = bootstrapConfig.CreateBootstrapLogger();

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .UseSerilog((context, loggerConfiguration) =>
        loggerConfiguration.ReadFrom.Configuration(context.Configuration))
    .ConfigureAppConfiguration((_, config) =>
        // Operator-managed overrides live outside the install directory so an
        // upgrade cannot wipe them; ConfigTool writes to this same path.
        config.AddJsonFile(ServicePaths.ConfigurationFile, optional: true, reloadOnChange: true))
    .UseContentRoot(AppContext.BaseDirectory)
    .UseServiceProviderFactory(new DefaultServiceProviderFactory(new ServiceProviderOptions
    {
        ValidateOnBuild = true,
        ValidateScopes = true
    }))
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.UseStartup<Startup>();
    });

try
{
    var app = builder.Build();
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
