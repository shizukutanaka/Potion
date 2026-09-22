using Potion.Service;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
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
app.Run();
