using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;

Console.WriteLine(
    $"Framework runtime={Environment.Version} aspnet={typeof(HttpContext).Assembly.GetName().Version}"
);
await ValidationProbe.Run(false, false);
await ValidationProbe.Run(false, true);
await ValidationProbe.Run(true, true);
await ExceptionProbe.Run(true, false);
await ExceptionProbe.Run(false, false);
await ExceptionProbe.Run(true, false, true);
#if NET10_0_OR_GREATER
await ExceptionProbe.Run(true, true);
#endif
Check.That(
    !AppDomain
        .CurrentDomain.GetAssemblies()
        .Any(assembly => assembly.GetName().Name!.StartsWith("Sentry", StringComparison.Ordinal)),
    "framework host runs without any Sentry assembly"
);
Console.WriteLine("PASS framework (Sentry absent)");

static class ValidationProbe
{
    public static async Task Run(bool mvc, bool problemDetails)
    {
        var builder = ProbeHost.Builder();
        var results = new ConcurrentDictionary<string, ValidationState>();
        if (mvc)
            builder.Services.AddControllers();
        if (problemDetails)
            builder.Services.AddProblemDetails();
#if NET10_0_OR_GREATER
        builder.Services.AddValidation();
#endif
        // Registering options callbacks does not install MVC services.
        builder.Services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            var original = options.InvalidModelStateResponseFactory;
            options.InvalidModelStateResponseFactory = context =>
            {
                var state = (ValidationState)context.HttpContext.Items[typeof(ValidationState)]!;
                state.FactoryCalls++;
                foreach (var pair in context.ModelState)
                {
                    var parameters = context.ActionDescriptor.Parameters;
                    var exact = parameters.FirstOrDefault(parameter =>
                        (parameter.BindingInfo?.BinderModelName ?? parameter.Name) == pair.Key
                    );
                    var source = exact?.BindingInfo?.BindingSource?.Id?.ToLowerInvariant() ?? "";
                    if (
                        source == ""
                        && parameters.Count == 1
                        && parameters[0].BindingInfo?.BindingSource == BindingSource.Body
                    )
                        source = "body";
                    foreach (var error in pair.Value!.Errors)
                        state.Details.Add(
                            new(
                                source,
                                pair.Key,
                                error.ErrorMessage.Length > 0
                                    ? error.ErrorMessage
                                    : error.Exception?.Message ?? "",
                                error.Exception?.GetType().FullName ?? ""
                            )
                        );
                }
                return original(context);
            };
        });
        builder.Services.PostConfigure<ProblemDetailsOptions>(options =>
        {
            var original = options.CustomizeProblemDetails;
            options.CustomizeProblemDetails = context =>
            {
                original?.Invoke(context);
                if (context.ProblemDetails is HttpValidationProblemDetails validation)
                {
                    var state = (ValidationState)
                        context.HttpContext.Items[typeof(ValidationState)]!;
                    state.ProblemCalls++;
                    if (state.Details.Count == 0)
                        state.Details.AddRange(Normalize(validation.Errors));
                }
            };
        });
        await using var app = builder.Build();
        Check.That(
            (app.Services.GetService<IActionDescriptorCollectionProvider>() != null) == mvc,
            "MVC is registered only for the controller host"
        );
        app.Use(
            async (context, next) =>
            {
                var state = new ValidationState();
                context.Items[typeof(ValidationState)] = state;
                var body = context.Response.Body;
                await using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await next(context);
                    state.Status = context.Response.StatusCode;
                    if (context.Request.Path == "/minimal/automatic" && !problemDetails)
                        Console.WriteLine(
                            $"automatic fallback: status={state.Status} contentType={context.Response.ContentType} body={System.Text.Encoding.UTF8.GetString(buffer.ToArray())}"
                        );
                    if (state.Details.Count == 0)
                        state.Details.AddRange(
                            ParseKnownResponse(
                                state.Status,
                                context.Response.ContentType,
                                buffer.ToArray()
                            )
                        );
                    results[context.Request.Path] = state;
                    buffer.Position = 0;
                    await buffer.CopyToAsync(body);
                }
                finally
                {
                    context.Response.Body = body;
                }
            }
        );
        app.MapGet(
            "/minimal/typed",
            () =>
                TypedResults.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["payload.literal.dot"] = ["synthetic validation"],
                    }
                )
        );
        app.MapGet(
            "/minimal/untyped",
            () =>
                Results.ValidationProblem(
                    new Dictionary<string, string[]>
                    {
                        ["items[a.b].name"] = ["synthetic validation"],
                    },
                    statusCode: 422
                )
        );
        app.MapGet(
            "/minimal/arbitrary",
            () =>
                Results.BadRequest(new { errors = new { name = new[] { "not known validation" } } })
        );
        app.MapGet(
            "/minimal/problem",
            () => Results.Problem(statusCode: 400, detail: "ordinary problem")
        );
        app.MapGet("/minimal/binding", (int count) => count);
        app.MapPost("/minimal/automatic", (ValidationInput input) => Results.Ok(input));
        if (mvc)
            app.MapControllers();
        await app.StartAsync();
        using var client = ProbeHost.Client(app);
        foreach (
            var path in new[]
            {
                "/minimal/typed",
                "/minimal/untyped",
                "/minimal/arbitrary",
                "/minimal/problem",
                "/minimal/binding?count=no",
            }
        )
        {
            using var response = await client.GetAsync(path);
            await response.Content.ReadAsStringAsync();
        }
        using (
            var response = await client.PostAsJsonAsync(
                "/minimal/automatic",
                new { address = new { }, entries = new { } }
            )
        )
            await response.Content.ReadAsStringAsync();
        Check.That(
            results["/minimal/typed"].Details.Single().Field == "payload.literal.dot",
            "typed dotted field preserved"
        );
        Check.That(
            results["/minimal/untyped"].Details.Single().Field == "items[a.b].name",
            "422 opaque field preserved"
        );
        foreach (var path in new[] { "/minimal/arbitrary", "/minimal/problem", "/minimal/binding" })
            Check.That(
                results[path].Status == 400 && results[path].Details.Count == 0,
                "ordinary 400 not validation: " + path
            );
#if NET9_0_OR_GREATER
        Check.That(
            results["/minimal/typed"].ProblemCalls == (problemDetails ? 1 : 0),
            "net9/10 typed validation calls registered problem-details service"
        );
#else
        Check.That(
            results["/minimal/typed"].ProblemCalls == 0,
            "net8 typed result bypasses problem-details service"
        );
#endif
#if NET10_0_OR_GREATER
        Check.That(
            results["/minimal/automatic"].Status == 400
                && results["/minimal/automatic"].ProblemCalls == (problemDetails ? 1 : 0)
                && results["/minimal/automatic"].Details.Count > 0,
            "net10 automatic validation recognized with and without problem-details service"
        );
#else
        Check.That(
            results["/minimal/automatic"].Status == 200,
            "net8/9 no built-in automatic Minimal API validation"
        );
#endif
        if (mvc)
        {
            using (
                var response = await client.PostAsJsonAsync(
                    "/mvc/body",
                    new
                    {
                        address = new { },
                        entries = new Dictionary<string, object> { ["a.b"] = new { } },
                    }
                )
            )
                await response.Content.ReadAsStringAsync();
            using (var response = await client.GetAsync("/mvc/query?limit.literal=invalid"))
                await response.Content.ReadAsStringAsync();
            var body = results["/mvc/body"];
            Check.That(
                body.Status == 400 && body.FactoryCalls == 1,
                "automatic MVC model state factory"
            );
            Check.That(
                body.Details.Any(detail =>
                    detail.Field == "Entries[0].Value.Name" && detail.Source == "body"
                ),
                "MVC supplied indexed dictionary field preserved"
            );
            Check.That(
                body.Details.All(detail => detail.Message.Length > 0),
                "MVC messages present"
            );
            Check.That(
                results["/mvc/query"].Details.Count > 0
                    && results["/mvc/query"].Details.All(detail => detail.Source == "query"),
                "MVC exact binding source"
            );
            Check.That(
                results["/mvc/query"].Details.All(detail => detail.Field == "limit.literal"),
                "MVC query alias preserved"
            );
        }
        Check.That(
            ParseKnownResponse(400, "application/problem+json", new byte[50_001]).Count == 0,
            "oversize skipped"
        );
        Check.That(
            ParseKnownResponse(400, "application/problem+json", "{\"errors\":"u8.ToArray()).Count
                == 0,
            "incomplete JSON skipped"
        );
        foreach (var pair in results.OrderBy(pair => pair.Key))
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        mvc,
                        problemDetails,
                        path = pair.Key,
                        pair.Value.Status,
                        pair.Value.FactoryCalls,
                        pair.Value.ProblemCalls,
                        pair.Value.Details,
                    }
                )
            );
        await app.StopAsync();
    }

    private static List<ValidationDetail> ParseKnownResponse(
        int status,
        string? contentType,
        byte[] bytes
    )
    {
        var mediaType = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (
            status is not (400 or 422)
            || mediaType is not ("application/problem+json" or "application/json")
            || bytes.Length > 50_000
        )
            return [];
        try
        {
            using var json = JsonDocument.Parse(bytes);
            var root = json.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("title", out var title)
                || title.ValueKind != JsonValueKind.String
                || title.GetString() != "One or more validation errors occurred."
                || !root.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Object
            )
                return [];
            var hasStatus = root.TryGetProperty("status", out var code);
            var hasType = root.TryGetProperty("type", out var type);
#if NET10_0_OR_GREATER
            var compact =
                status == 400 && mediaType == "application/json" && !hasStatus && !hasType;
#else
            var compact = false;
#endif
            var standard =
                mediaType == "application/problem+json"
                && hasStatus
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out var value)
                && value == status
                && hasType
                && type.ValueKind == JsonValueKind.String
                && type.GetString()
                    == (
                        status == 400
                            ? "https://tools.ietf.org/html/rfc9110#section-15.5.1"
                            : "https://tools.ietf.org/html/rfc4918#section-11.2"
                    );
            if (!compact && !standard)
                return [];
            var details = new List<ValidationDetail>();
            foreach (var property in errors.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                    return [];
                foreach (var message in property.Value.EnumerateArray())
                {
                    if (message.ValueKind != JsonValueKind.String)
                        return [];
                    details.Add(new("", property.Name, message.GetString()!, ""));
                }
            }
            return details;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IEnumerable<ValidationDetail> Normalize(IDictionary<string, string[]> errors) =>
        errors.SelectMany(pair =>
            pair.Value.Select(message => new ValidationDetail("", pair.Key, message, ""))
        );
}

static class ExceptionProbe
{
    public static async Task Run(
        bool observerFirst,
        bool forceDiagnostics,
        bool useDelegate = false
    )
    {
        using var diagnostics = new HandledDiagnostics();
        var builder = ProbeHost.Builder();
        builder.Services.AddProblemDetails();
        if (observerFirst)
            builder.Services.AddExceptionHandler<ObservingExceptionHandler>();
        if (!useDelegate)
            builder.Services.AddExceptionHandler<HandlingExceptionHandler>();
        if (!observerFirst)
            builder.Services.AddExceptionHandler<ObservingExceptionHandler>();
        var completed = new ConcurrentDictionary<string, TaskCompletionSource<ExceptionState>>();
        await using var app = builder.Build();
        app.Use(
            async (context, next) =>
            {
                var state = new ExceptionState();
                context.Items[typeof(ExceptionState)] = state;
                try
                {
                    await next(context);
                }
                catch (Exception exception)
                {
                    state.Escaping++;
                    state.Capture(exception, context);
                    throw;
                }
                finally
                {
                    var feature = context.Features.Get<IExceptionHandlerFeature>();
                    state.FeaturePresent = feature != null;
                    if (feature != null)
                    {
                        state.Capture(feature.Error, context);
                        state.Route = (feature.Endpoint as RouteEndpoint)?.RoutePattern.RawText;
                    }
                    state.Status = context.Response.StatusCode;
                    state.ServerGroup = state.Status == 500 && state.First != null;
                    completed[context.Request.Path].TrySetResult(state);
                }
            }
        );
        app.UseExceptionHandler(
            new ExceptionHandlerOptions
            {
                ExceptionHandler = useDelegate
                    ? async context =>
                    {
                        await new HandlingExceptionHandler().TryHandleAsync(
                            context,
                            context.Features.Get<IExceptionHandlerFeature>()!.Error,
                            context.RequestAborted
                        );
                    }
                    : null,
#if NET10_0_OR_GREATER
                SuppressDiagnosticsCallback = forceDiagnostics ? _ => false : null
#endif
            }
        );
        app.MapGet(
            "/error/{status:int}",
            (HttpContext context, int status) =>
                Throw(new InvalidOperationException("synthetic handled exception"))
        );
        app.MapGet("/intentional", () => Results.StatusCode(500));
        app.MapGet(
            "/first",
            (HttpContext context) =>
            {
                var state = (ExceptionState)context.Items[typeof(ExceptionState)]!;
                state.Capture(new ArgumentException("first"), context);
                state.Capture(new InvalidOperationException("ignored explicit"), context);
                return Throw(new InvalidOperationException("ignored automatic"));
            }
        );
        app.MapGet(
            "/aggregate",
            () => Throw(new AggregateException(new ArgumentException("single leaf")))
        );
        app.MapGet(
            "/unrelated-cancel",
            () => Throw(new OperationCanceledException("not request cancellation"))
        );
        var cancellationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        app.MapGet(
            "/cancel",
            async (HttpContext context) =>
            {
                cancellationStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
        );
        await app.StartAsync();
        using var client = ProbeHost.Client(app);
        foreach (
            var path in new[]
            {
                "/error/500",
                "/error/503",
                "/error/200",
                "/intentional",
                "/first",
                "/aggregate",
                "/unrelated-cancel",
                "/cancel",
            }
        )
        {
            var completion = new TaskCompletionSource<ExceptionState>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            completed[path] = completion;
            var diagnosticBefore = diagnostics.Count;
            if (path == "/cancel")
            {
                using var cancel = new CancellationTokenSource();
                var pending = client.GetAsync(path, cancel.Token);
                await cancellationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                cancel.Cancel();
                try
                {
                    using var response = await pending;
                    throw new Exception("expected client cancellation");
                }
                catch (OperationCanceledException) { }
            }
            else
            {
                using var response = await client.GetAsync(path);
                await response.Content.ReadAsStringAsync();
            }
            var state = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var diagnosticCount = diagnostics.Count - diagnosticBefore;
            Check.That(state.Escaping == 0, "escaping-only middleware sees no handled exception");
            if (path == "/intentional" || path == "/cancel")
            {
                Check.That(
                    state.First == null && !state.ServerGroup && !state.FeaturePresent,
                    "no exception group: " + path
                );
                Check.That(diagnosticCount == 0, "no handled diagnostic: " + path);
                if (path == "/cancel")
                    Check.That(state.Status == 499, "request cancellation classified by framework");
            }
            else
            {
                Check.That(
                    state.FeaturePresent && state.First != null,
                    "feature captures handled exception"
                );
                Check.That(
                    state.ObserverCalls == (observerFirst ? 1 : 0),
                    "IExceptionHandler registration ordering"
                );
                Check.That(state.Captures == 1, "one exception retained");
                Check.That(state.ServerGroup == (state.Status == 500), "exact final 500 only");
#if NET10_0_OR_GREATER
                Check.That(
                    diagnosticCount == (forceDiagnostics || useDelegate ? 1 : 0),
                    "net10 diagnostics suppression applies by default only to IExceptionHandler"
                );
#else
                Check.That(diagnosticCount == 1, "net8/9 handled diagnostic emitted");
#endif
                if (path == "/first")
                    Check.That(state.First!.Message == "first", "first capture wins");
                if (path == "/aggregate")
                    Check.That(state.First is ArgumentException, "single-leaf aggregate unwrapped");
                if (path.StartsWith("/error/"))
                    Check.That(
                        state.Route == "/error/{status:int}",
                        "original endpoint retained in exception feature"
                    );
            }
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        observerFirst,
                        forceDiagnostics,
                        useDelegate,
                        path,
                        state.Status,
                        state.FeaturePresent,
                        state.Escaping,
                        state.ObserverCalls,
                        diagnosticCount,
                        state.ServerGroup,
                        exception = state.First?.GetType().Name,
                        message = state.First?.Message,
                        state.Route,
                    }
                )
            );
        }
        await app.StopAsync();
    }

    private static IResult Throw(Exception exception) => throw exception;
}

public sealed class ObservingExceptionHandler : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        var state = (ExceptionState)context.Items[typeof(ExceptionState)]!;
        state.ObserverCalls++;
        state.Capture(exception, context);
        return ValueTask.FromResult(false);
    }
}

public sealed class HandlingExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context,
        Exception exception,
        CancellationToken cancellationToken
    )
    {
        var path = context.Features.Get<IExceptionHandlerPathFeature>()!.Path;
        context.Response.StatusCode = path.StartsWith("/error/") ? int.Parse(path[7..]) : 500;
        await context.Response.WriteAsync("synthetic handled response", cancellationToken);
        return true;
    }
}

public sealed class ExceptionState
{
    public Exception? First;
    public int Captures;
    public int ObserverCalls;
    public int Escaping;
    public int Status;
    public bool FeaturePresent;
    public bool ServerGroup;
    public string? Route;

    public void Capture(Exception exception, HttpContext context)
    {
        if (
            exception is AggregateException aggregate
            && aggregate.Flatten().InnerExceptions.Count == 1
        )
            exception = aggregate.Flatten().InnerExceptions[0];
        if (
            First != null
            || (
                exception is OperationCanceledException or IOException
                && context.RequestAborted.IsCancellationRequested
            )
        )
            return;
        First = exception;
        Captures++;
    }
}

public sealed class HandledDiagnostics
    : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>,
        IDisposable
{
    private readonly List<IDisposable> subscriptions = [];
    public int Count;

    public HandledDiagnostics() =>
        subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));

    public void OnNext(DiagnosticListener value)
    {
        if (value.Name == "Microsoft.AspNetCore")
            subscriptions.Add(
                value.Subscribe(
                    this,
                    name => name == "Microsoft.AspNetCore.Diagnostics.HandledException"
                )
            );
    }

    public void OnNext(KeyValuePair<string, object?> value) => Interlocked.Increment(ref Count);

    public void OnCompleted() { }

    public void OnError(Exception error) => throw error;

    public void Dispose()
    {
        foreach (var subscription in subscriptions)
            subscription.Dispose();
    }
}

public sealed record ValidationDetail(string Source, string Field, string Message, string Type);

public sealed class ValidationState
{
    public int Status;
    public int FactoryCalls;
    public int ProblemCalls;
    public List<ValidationDetail> Details = [];
}

[ApiController]
[Route("mvc")]
public sealed class ValidationController : ControllerBase
{
    [HttpPost("body")]
    public IActionResult Body([FromBody] ValidationInput input) => Ok(input);

    [HttpGet("query")]
    public IActionResult Query([FromQuery(Name = "limit.literal"), BindRequired] int limit) =>
        Ok(limit);
}

public sealed class ValidationInput
{
    public AddressInput Address { get; set; } = new();
    public Dictionary<string, AddressInput> Entries { get; set; } = [];
}

public sealed class AddressInput
{
    [Required]
    public string? Name { get; set; }
}

static class ProbeHost
{
    public static WebApplicationBuilder Builder()
    {
        var builder = WebApplication.CreateEmptyBuilder(
            new WebApplicationOptions { Args = [], EnvironmentName = "Production" }
        );
        builder.Configuration.AddInMemoryCollection();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        return builder;
    }

    public static HttpClient Client(WebApplication app) =>
        new() { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(15) };
}

static class Check
{
    public static void That(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("FAIL: " + message);
    }
}
