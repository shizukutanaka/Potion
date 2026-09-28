using Potion.Service;
using Potion.Service.Infrastructure;
using Serilog;

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
