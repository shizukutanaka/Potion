using Potion.Service;

var builder = Host.CreateDefaultBuilder(args)
    .UseWindowsService()
    .UseContentRoot(AppContext.BaseDirectory)
    .ConfigureWebHostDefaults(webBuilder =>
    {
        webBuilder.UseStartup<Startup>();
    });

var app = builder.Build();
app.Run();
