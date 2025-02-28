using Apitally;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddApitallyWithoutBackgroundServices();
builder.Services.Configure<ApitallyOptions>(options =>
{
    options.ClientId = "00000000-0000-0000-0000-000000000000";
    options.Env = "test";
    options.RequestLogging.Enabled = true;
    options.RequestLogging.ShouldExclude = (request, response) =>
    {
        return false;
    };
});

var app = builder.Build();
app.UseApitally();

app.MapGet(
        "/items",
        () =>
        {
            var items = new[]
            {
                new { Id = 1, Name = "Item 1" },
                new { Id = 2, Name = "Item 2" },
                new { Id = 3, Name = "Item 3" },
            };
            return items;
        }
    )
    .WithName("GetItems");

app.MapPost("/items", (Item item) => Results.Created($"/items/{item.Id}", item))
    .WithName("CreateItem");

app.MapGet(
        "/error",
        () =>
        {
            throw new Exception("An expected error occurred");
        }
    )
    .WithName("ThrowError");

app.Run();

public partial class TestApp { }

record Item(int Id, string Name);
