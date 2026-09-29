namespace Apitally.TestApp;

public static class Program
{
    public static void Main(string[] args) => CreateMinimalApp(args).Run();

    // Modern hosting with WebApplicationBuilder.
    public static WebApplication CreateMinimalApp(
        string[] args,
        Action<WebApplicationBuilder>? configure = null
    )
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddControllers().AddApplicationPart(typeof(Program).Assembly);
        configure?.Invoke(builder);
        // Registered last so a test's own AddApitally call controls registration order.
        builder.Services.AddApitally();
        var app = builder.Build();
        app.MapTestRoutes();
        app.MapControllers();
        return app;
    }

    // Generic Host with a Startup class.
    public static IHostBuilder CreateStartupHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureWebHostDefaults(webBuilder => webBuilder.UseStartup<Startup>());
}
