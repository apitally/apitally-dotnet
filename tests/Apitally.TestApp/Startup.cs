namespace Apitally.TestApp;

public sealed class Startup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddApitally();
        services.AddControllers().AddApplicationPart(typeof(Startup).Assembly);
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapTestRoutes();
            endpoints.MapControllers();
        });
    }
}
