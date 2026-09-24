using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var builder = WebApplication.CreateEmptyBuilder(
    new WebApplicationOptions { Args = [], EnvironmentName = "Production" }
);
builder.Configuration.AddInMemoryCollection();
builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
builder.Services.AddRouting();
builder.Services.AddControllers().AddApplicationPart(typeof(MetadataController).Assembly);
await using var app = builder.Build();
app.MapGet("/minimal", () => "synthetic")
    .WithSummary("Minimal summary")
    .WithDescription("Minimal description");
app.MapMethods("/multiple-methods", ["GET", "POST"], () => "synthetic")
    .WithSummary("Multiple methods summary");
app.MapGet("/undocumented", () => "synthetic");
var group = app.MapGroup("/group")
    .WithSummary("Group summary")
    .WithDescription("Group description");
group.MapGet("/inherited", () => "synthetic");
group.MapGet("/overridden", () => "synthetic").WithSummary("Endpoint summary");
app.MapControllers();
await app.StartAsync(timeout.Token);

var paths = app
    .Services.GetRequiredService<EndpointDataSource>()
    .Endpoints.OfType<RouteEndpoint>()
    .SelectMany(endpoint =>
        (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(
            method => new
            {
                method,
                path = "/" + endpoint.RoutePattern.RawText!.TrimStart('/'),
                summary = endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary,
                description = endpoint
                    .Metadata.GetMetadata<IEndpointDescriptionMetadata>()
                    ?.Description,
            }
        )
    )
    .ToArray();
Console.WriteLine($"Runtime: {Environment.Version}");
Console.WriteLine(JsonSerializer.Serialize(paths));

Check("/minimal", "Minimal summary", "Minimal description");
Check("/undocumented", null, null);
Check("/group/inherited", "Group summary", "Group description");
Check("/group/overridden", "Endpoint summary", "Group description");
Check("/controller/documented", "Action summary", "Action description");
Check("/controller/undocumented", null, null);
foreach (var method in new[] { "GET", "POST" })
{
    var path = paths.Single(value => value.path == "/multiple-methods" && value.method == method);
    if (path.summary != "Multiple methods summary" || path.description != null)
        throw new InvalidOperationException("Multiple-method metadata mismatch");
}
var optionalAssemblies = AppDomain
    .CurrentDomain.GetAssemblies()
    .Select(assembly => assembly.GetName().Name!)
    .Where(name =>
        name.StartsWith("Microsoft.OpenApi")
        || name.StartsWith("Microsoft.AspNetCore.OpenApi")
        || name.StartsWith("Swashbuckle")
        || name.StartsWith("NSwag")
    )
    .ToArray();
if (optionalAssemblies.Length != 0)
    throw new InvalidOperationException(
        "Unexpected generator assembly: " + string.Join(",", optionalAssemblies)
    );
await app.StopAsync(timeout.Token);
Console.WriteLine(
    "PASS: 8 path/method cases; no OpenAPI or third-party generator assemblies loaded"
);

void Check(string route, string? summary, string? description)
{
    var path = paths.Single(value => value.path == route);
    if (path.summary != summary || path.description != description)
        throw new InvalidOperationException($"Metadata mismatch: {route}");
}

[ApiController]
[Route("controller")]
public sealed class MetadataController : ControllerBase
{
    [HttpGet("documented")]
    [EndpointSummary("Action summary")]
    [EndpointDescription("Action description")]
    public string Documented() => "synthetic";

    [HttpGet("undocumented")]
    public string Undocumented() => "synthetic";
}
