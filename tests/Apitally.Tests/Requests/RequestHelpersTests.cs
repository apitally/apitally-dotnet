using Apitally.Requests;
using Apitally.Tests.Support;
using Microsoft.AspNetCore.Http;

namespace Apitally.Tests.Requests;

public class RequestHelpersTests
{
    [Fact]
    public void HelpersAreNoOpsOutsideAMonitoredRequest()
    {
        var helpers = new RequestHelpers(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }
        );

        helpers.SetConsumer("acme", "Acme");
        helpers.SetRequestAttribute("key", "value");
        helpers.CaptureException(new InvalidOperationException());
    }

    [Fact]
    public async Task ConsumerIsAttributedAndUpdatedIndependentlyOfSampling()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(
            receiver,
            builder => builder.Services.AddApitally(options => options.SampleRate = 0)
        );

        await host.Client.GetAsync("/consumers/acme");
        await host.Client.GetAsync("/consumers/acme");
        await host.StopAsync();

        Assert.Empty(receiver.Spans());
        var update = Assert.Single(receiver.Events("apitally.consumer.update"));
        Assert.True(update.TraceId.IsEmpty);
        Assert.Empty(update.Attributes);
        Assert.Equal(
            new Dictionary<string, object?>
            {
                ["identifier"] = "acme",
                ["name"] = "Consumer acme",
                ["group"] = "customers",
                ["attributes"] = new Dictionary<string, object?> { ["plan"] = "pro" },
            },
            OtlpDecoding.Value(update.Body)
        );
    }

    [Fact]
    public async Task ConsumerIdentifierIsSetOnServerSpan()
    {
        await using var receiver = await OtlpReceiver.StartAsync();
        await using var host = await ApplicationHost.StartMinimalAsync(receiver);

        await host.Client.GetAsync("/consumers/acme");
        await host.StopAsync();

        var attributes = Assert.Single(receiver.Spans()).Attributes();
        Assert.Equal("acme", attributes["apitally.consumer.identifier"]);
        Assert.False(attributes.ContainsKey("apitally.consumer.name"));
    }
}
