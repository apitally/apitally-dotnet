using Apitally.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Apitally.Hosting;

// Wraps the application pipeline. GenericWebHostService builds the pipeline before starting
// the server, so activation completes before the first request can arrive.
internal sealed class ApitallyStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            var runtime = app.ApplicationServices.GetRequiredService<TelemetryRuntime>();
            runtime.Prepare(app.ApplicationServices);
            if (runtime.IsPrepared)
                app.UseMiddleware<ApitallyMiddleware>();
            next(app);
            runtime.Activate(app.ApplicationServices);
        };
}
