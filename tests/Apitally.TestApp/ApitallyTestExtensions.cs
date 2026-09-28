namespace Apitally.TestApp;

public static class ApitallyTestExtensions
{
    public static IEndpointRouteBuilder MapTestRoutes(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/hello", () => "Hello").WithDescription("Says hello");
        endpoints.MapGet(
            "/items/{id:int}",
            (int id, IApitally apitally) =>
            {
                using var activity = apitally.StartActivity("load-item");
                apitally.SetRequestAttribute("item.id", id);
                return Results.Ok(new { id });
            }
        );
        endpoints.MapPost("/items", (Item item) => Results.Created($"/items/{item.Id}", item));
        endpoints.MapGet(
            "/consumers/{identifier}",
            (string identifier, IApitally apitally) =>
            {
                apitally.SetConsumer(
                    identifier,
                    name: $"Consumer {identifier}",
                    group: "customers",
                    attributes: new Dictionary<string, string?> { ["plan"] = "pro" }
                );
                return "OK";
            }
        );
        endpoints.MapGet("/error", string () => throw new InvalidOperationException("Test error"));
        endpoints.MapGet("/status/{code:int}", (int code) => Results.StatusCode(code));
        endpoints.MapGet(
            "/validation",
            () =>
                Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["items[a.b].name"] = ["Name is required."] }
                )
        );
        endpoints.MapGet(
            "/validation/422",
            () =>
                Results.ValidationProblem(
                    new Dictionary<string, string[]> { ["email"] = ["Ungültige E-Mail-Adresse."] },
                    statusCode: 422
                )
        );
        endpoints.MapGet(
            "/validation/custom",
            () => Results.BadRequest(new { errors = new { name = new[] { "Not a known shape" } } })
        );
        var group = endpoints.MapGroup("/api/v1");
        group.MapGet("/", () => "API");
        group.MapGet("/orders/{orderId}", (string orderId) => Results.Ok(new { orderId }));
        return endpoints;
    }
}

public sealed record Item(int Id, string Name);
