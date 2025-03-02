using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using Apitally;
using Apitally.TestApp;
using Microsoft.AspNetCore.Mvc;

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
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    });
});
app.UseApitally();

app.MapGet(
        "/items",
        (HttpContext context, [FromQuery] [StringLength(10, MinimumLength = 2)] string? name) =>
        {
            context.Items["ApitallyConsumer"] = new ApitallyConsumer
            {
                Identifier = "tester",
                Name = "Tester",
                Group = "Test Group",
            };

            var items = new[] { new Item(1, "bob"), new Item(2, "alice") };
            return items;
        }
    )
    .WithName("GetItems");

app.MapGet(
        "/items/{id:min(1)}",
        (int id) =>
        {
            return new Item(id, "bob");
        }
    )
    .WithName("GetItem");

app.MapPost("/items", (Item item) => Results.Created($"/items/{item.Id}", item))
    .WithName("CreateItem");

app.MapPut(
        "/items/{id:min(1)}",
        (int id, Item item) =>
        {
            return Results.NoContent();
        }
    )
    .WithName("UpdateItem");

app.MapDelete(
        "/items/{id:min(1)}",
        (int id) =>
        {
            return Results.NoContent();
        }
    )
    .WithName("DeleteItem");

app.MapGet(
        "/throw",
        () =>
        {
            throw new TestException("an expected error occurred");
        }
    )
    .WithName("ThrowError");

app.Run();

public partial class Program { }

#pragma warning disable CA1050 // Declare types in namespaces
public record Item
{
    public Item(int id, string name)
    {
        Id = id;
        Name = name;
    }

    [Range(1, int.MaxValue)]
    public int Id { get; init; }

    [Required]
    [StringLength(10, MinimumLength = 2)]
    public string Name { get; init; }
}

class TestException : Exception
{
    public TestException(string? message)
        : base(message) { }
}
