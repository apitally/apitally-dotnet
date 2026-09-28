using Apitally.AspNetCore;
using Apitally.Requests;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Apitally.Tests.Requests;

public class ErrorAggregatesTests
{
    [Fact]
    public async Task ServerErrorsRequireAnExceptionAndFinalStatus500()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder => builder.Services.AddApitally(options => options.ExcludePaths = ["^/error$"]),
            app =>
            {
                app.UseExceptionHandler(error =>
                    error.Run(context =>
                    {
                        var exception = context.Features.Get<IExceptionHandlerFeature>()!.Error;
                        context.Response.StatusCode = exception is TimeoutException ? 503 : 500;
                        return Task.CompletedTask;
                    })
                );
                app.MapGet("/unavailable", string () => throw new TimeoutException());
                app.MapGet(
                    "/captured",
                    (IApitally apitally) =>
                    {
                        apitally.CaptureException(
                            new AggregateException(new ArgumentException("first"))
                        );
                        apitally.CaptureException(new InvalidOperationException("second"));
                        return Results.StatusCode(500);
                    }
                );
            }
        );

        await host.Client.GetAsync("/error");
        await host.Client.GetAsync("/error");
        await host.Client.GetAsync("/unavailable");
        await host.Client.GetAsync("/status/500");
        await host.Client.GetAsync("/captured");
        await host.Client.GetAsync("/missing-route-throws");
        await host.StopAsync();

        var events = receiver
            .Events("apitally.request.server_error")
            .Select(e => (Dictionary<string, object?>)OtlpDecoding.Value(e.Body)!)
            .OrderBy(e => (string)e["path"]!)
            .ToList();
        Assert.Equal(["/captured", "/error"], events.Select(e => e["path"]));
        Assert.Equal("System.ArgumentException", events[0]["type"]);
        Assert.Equal("first", events[0]["message"]);
        Assert.Equal("System.InvalidOperationException", events[1]["type"]);
        Assert.Equal("Test error", events[1]["message"]);
        Assert.Contains("Test error", (string)events[1]["stacktrace"]!);
        Assert.Equal(2L, events[1]["count"]);
        Assert.Equal("GET", events[1]["method"]);
    }

    [Fact]
    public async Task DeveloperExceptionPageResponsesKeepTheException()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            arguments: "--environment=Development"
        );

        var response = await host.Client.GetAsync("/error");
        await host.StopAsync();

        Assert.Equal(500, (int)response.StatusCode);
        var server = Assert.Single(receiver.Spans());
        Assert.Equal("exception", Assert.Single(server.Events).Name);
        var error = Assert.Single(receiver.Events("apitally.request.server_error"));
        Assert.Equal(
            "/error",
            ((Dictionary<string, object?>)OtlpDecoding.Value(error.Body)!)["path"]
        );
    }

    [Fact]
    public void GroupsAreBoundedBetweenDrains()
    {
        var aggregates = new ErrorAggregates();
        for (var i = 0; i < 150; i++)
            aggregates.AddValidationErrors(
                null,
                "POST",
                "/a",
                [new ValidationDetail("body", $"f{i}", "m", "")]
            );
        aggregates.AddValidationErrors(
            null,
            "POST",
            "/a",
            [new ValidationDetail("body", "f0", "m", "")]
        );

        var (validation, server) = aggregates.Drain();

        Assert.Equal(ErrorAggregates.MaxGroups, validation.Count);
        Assert.Equal(2L, validation.Single(e => (string)e["field"]! == "f0")["count"]);
        Assert.Empty(server);
        Assert.Empty(aggregates.Drain().Validation);
    }

    [Fact]
    public void ValuesAreTruncatedBeforeKeying()
    {
        var aggregates = new ErrorAggregates();

        aggregates.AddValidationErrors(
            new string('c', 200),
            "POST",
            "/b",
            [
                new ValidationDetail(
                    new string('s', 50),
                    "f",
                    new string('m', 3_000) + "a",
                    new string('t', 200)
                ),
            ]
        );
        aggregates.AddValidationErrors(
            new string('c', 200),
            "POST",
            "/b",
            [
                new ValidationDetail(
                    new string('s', 50),
                    "f",
                    new string('m', 3_000) + "b",
                    new string('t', 200)
                ),
            ]
        );

        var body = Assert.Single(aggregates.Drain().Validation);
        Assert.Equal(128, ((string)body["consumer"]!).Length);
        Assert.Equal(32, ((string)body["source"]!).Length);
        Assert.Equal(2_048, ((string)body["message"]!).Length);
        Assert.Equal(128, ((string)body["type"]!).Length);
        Assert.Equal(2L, body["count"]);
    }
}
