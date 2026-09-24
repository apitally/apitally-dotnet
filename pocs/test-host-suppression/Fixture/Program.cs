using System.Net;

namespace TestHostSuppression;

public sealed class FixtureProgram
{
    public static async Task Main(string[] args)
    {
        var builder = CreateBuilder();
        await using var app = builder.Build();
        app.MapGet("/probe", () => "synthetic-response");
        await app.RunAsync();
    }

    public static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions
            {
                Args = [],
                EnvironmentName = Environments.Development,
                ApplicationName = typeof(FixtureProgram).Assembly.GetName().Name,
                ContentRootPath = AppContext.BaseDirectory,
            }
        );
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        Candidate.Register(builder.Services);
        return builder;
    }
}

public sealed class FixtureStartup
{
    public void ConfigureServices(IServiceCollection services)
    {
        services.AddRouting();
        Candidate.Register(services);
    }

    public void Configure(IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseEndpoints(endpoints => endpoints.MapGet("/probe", () => "synthetic-response"));
    }
}
