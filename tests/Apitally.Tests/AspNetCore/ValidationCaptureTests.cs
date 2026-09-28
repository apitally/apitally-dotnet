using System.Net.Http.Json;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Proto.Logs.V1;

namespace Apitally.Tests.AspNetCore;

public class ValidationCaptureTests
{
    [Fact]
    public async Task MvcModelValidationIsCapturedIndependentlyOfSampling()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder => builder.Services.AddApitally(options => options.SampleRate = 0)
        );

        await host.Client.PostAsJsonAsync("/controller/items", new { id = 5000, name = "a" });
        await host.Client.GetAsync("/controller/items?limit=invalid");
        await host.StopAsync();

        Assert.Empty(receiver.Spans());
        var events = Bodies(receiver.Events("apitally.request.validation_error"));
        var body = Assert.Single(events, e => (string)e["method"]! == "POST");
        Assert.Equal("body", body["source"]);
        Assert.Equal("Id", body["field"]);
        Assert.Equal("The field Id must be between 1 and 1000.", body["message"]);
        Assert.Equal("", body["type"]);
        Assert.Equal(1L, body["count"]);
        Assert.False(body.ContainsKey("consumer"));
        var query = events.Where(e => (string)e["method"]! == "GET").ToList();
        Assert.NotEmpty(query);
        Assert.All(query, e => Assert.Equal(("query", "limit"), (e["source"], e["field"])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MinimalValidationProblemsAreCapturedWithAndWithoutProblemDetails(
        bool addProblemDetails
    )
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder =>
            {
                if (addProblemDetails)
                    builder.Services.AddProblemDetails();
            }
        );

        await host.Client.GetAsync("/validation");
        await host.Client.GetAsync("/validation");
        await host.Client.GetAsync("/validation/422");
        await host.StopAsync();

        var events = Bodies(receiver.Events("apitally.request.validation_error"))
            .OrderBy(e => (string)e["path"]!)
            .ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["method"] = "GET",
                ["path"] = "/validation",
                ["source"] = "",
                ["field"] = "items[a.b].name",
                ["message"] = "Name is required.",
                ["type"] = "",
                ["count"] = 2L,
            },
            events[0]
        );
        Assert.Equal("Ungültige E-Mail-Adresse.", events[1]["message"]);
        Assert.Equal(1L, events[1]["count"]);
        Assert.All(
            receiver.Spans(),
            span => Assert.False(span.Attributes().ContainsKey("apitally.response.body"))
        );
    }

    [Fact]
    public async Task UnknownResponseShapesAreNotCounted()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);

        await host.Client.GetAsync("/validation/custom");
        await host.Client.GetAsync("/status/400");
        await host.StopAsync();

        Assert.Empty(receiver.Events("apitally.request.validation_error"));
    }

#if NET10_0_OR_GREATER
    [Fact]
    public async Task BuiltInMinimalApiValidationIsCaptured()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder => builder.Services.AddValidation(),
            app => app.MapPost("/validated", (TestApp.ItemInput input) => Results.Ok(input))
        );

        await host.Client.PostAsJsonAsync("/validated", new { id = 1 });
        await host.StopAsync();

        var body = Assert.Single(Bodies(receiver.Events("apitally.request.validation_error")));
        Assert.Equal("/validated", body["path"]);
        Assert.Equal("Name", body["field"]);
    }
#endif

    private static List<Dictionary<string, object?>> Bodies(IEnumerable<LogRecord> events) =>
        events.Select(e => (Dictionary<string, object?>)OtlpDecoding.Value(e.Body)!).ToList();
}
