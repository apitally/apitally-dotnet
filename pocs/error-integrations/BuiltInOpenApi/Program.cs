using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.OpenApi;
#if NET10_0_OR_GREATER
using Microsoft.AspNetCore.OpenApi;
#else
using Microsoft.OpenApi.Models;
#endif

Console.WriteLine($"Built-in OpenAPI runtime={Environment.Version}");
var builder = WebApplication.CreateEmptyBuilder(
    new WebApplicationOptions { Args = [], EnvironmentName = "Production" }
);
builder.Configuration.AddInMemoryCollection();
builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
builder.Services.AddRouting();
#if NET9_0_OR_GREATER
builder.Services.AddOpenApi(
    "v1",
    options =>
        options.AddDocumentTransformer(
            (document, context, token) =>
            {
                document.Info.Title = "Synthetic transformed schema";
                return Task.CompletedTask;
            }
        )
);
#endif
await using var app = builder.Build();
var group = app.MapGroup("/api").MapGroup("/v1");
var get = group.MapGet("/items/{id:int}", (int id) => new Item(id)).WithName("GetItem");
#if NET8_0
get.WithOpenApi();
#endif
group
    .MapMethods("/items/{id:int}", ["PUT", "PATCH"], (int id) => new Item(id))
    .WithName("UpdateItem");
app.MapGet("/health", () => "ok");
await app.StartAsync();
var endpoints = app
    .Services.GetRequiredService<EndpointDataSource>()
    .Endpoints.OfType<RouteEndpoint>()
    .ToArray();
var paths = endpoints
    .SelectMany(endpoint =>
        (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(
            method => new { method, path = endpoint.RoutePattern.RawText }
        )
    )
    .ToArray();
Console.WriteLine(JsonSerializer.Serialize(paths));
Assert(
    paths.Any(path => path.method == "GET" && path.path == "/api/v1/items/{id:int}"),
    "finalized group prefix and route constraint"
);
Assert(paths.Count(path => path.path == "/api/v1/items/{id:int}") == 3, "multiple method metadata");
#if NET10_0_OR_GREATER
using var scope = app.Services.CreateScope();
var provider = scope.ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
var document = await provider.GetOpenApiDocumentAsync();
using var buffer = new MemoryStream();
await document.SerializeAsJsonAsync(buffer, OpenApiSpecVersion.OpenApi3_0);
var json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
using var parsed = JsonDocument.Parse(json);
Assert(
    parsed.RootElement.GetProperty("paths").TryGetProperty("/api/v1/items/{id}", out _),
    "public in-process schema generation"
);
Assert(
    document.Info.Title == "Synthetic transformed schema",
    "public provider applies document transformer"
);
Console.WriteLine(
    $"public IOpenApiDocumentProvider: schema bytes={System.Text.Encoding.UTF8.GetByteCount(json)} paths={document.Paths.Count}"
);
#elif NET9_0_OR_GREATER
var publicTypes = typeof(Microsoft.AspNetCore.OpenApi.OpenApiOptions).Assembly.GetExportedTypes();
Assert(
    !publicTypes.Any(type =>
        type.Name is "IOpenApiDocumentProvider" or "OpenApiDocumentService" or "IDocumentProvider"
    ),
    "net9 document provider types are not public"
);
Console.WriteLine(
    "NEGATIVE net9: document generation services are internal; public transformers do not initiate generation"
);
#else
var endpoint = endpoints.Single(endpoint =>
    endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetItem"
);
Assert(
    endpoint.Metadata.GetMetadata<OpenApiOperation>() != null,
    "net8 public per-operation metadata"
);
Console.WriteLine("net8 WithOpenApi exposes operation metadata, not a built-in document generator");
#endif
await app.StopAsync();
Console.WriteLine("PASS built-in OpenAPI (no schema endpoint mapped or requested)");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException("FAIL: " + message);
}

public sealed record Item(int Id);
