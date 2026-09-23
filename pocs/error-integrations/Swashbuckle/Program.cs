using System.Text.Json;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

Console.WriteLine($"Swashbuckle runtime={Environment.Version} package=10.2.3");
var builder = WebApplication.CreateEmptyBuilder(
    new WebApplicationOptions { Args = [], EnvironmentName = "Production" }
);
builder.Configuration.AddInMemoryCollection();
builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
builder.Services.AddRouting();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Synthetic API", Version = "v1" })
);
await using var app = builder.Build();
app.MapGroup("/api/v1").MapGet("/items/{id:int}", (int id) => new Item(id));
await app.StartAsync();
using var scope = app.Services.CreateScope();
var document = scope.ServiceProvider.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
using var buffer = new MemoryStream();
await document.SerializeAsJsonAsync(buffer, OpenApiSpecVersion.OpenApi3_0);
using var json = JsonDocument.Parse(buffer.ToArray());
if (!json.RootElement.GetProperty("paths").TryGetProperty("/api/v1/items/{id}", out _))
    throw new InvalidOperationException("FAIL: schema route missing");
Console.WriteLine(
    $"public ISwaggerProvider: schema bytes={buffer.Length} paths={document.Paths.Count}"
);
await app.StopAsync();
Console.WriteLine("PASS Swashbuckle (no schema endpoint mapped or requested)");

public sealed record Item(int Id);
