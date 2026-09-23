using Potion.Service;
using Serilog;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .UseSerilog((context, loggerConfiguration) =>
        loggerConfiguration.ReadFrom.Configuration(context.Configuration))
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

var app = builder.Build();
try
{
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
