using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Apitally.AspNetCore;
using Apitally.Export;
using Apitally.Hosting;
using Apitally.Requests;
using Microsoft.AspNetCore.Http;

namespace Apitally.Logging;

// SDK-owned log events. They bypass the application logger provider, so application masking
// and truncation never apply to them.
internal sealed class InternalEvents(
    ApitallyBatchProcessor<LogSnapshot> logProcessor,
    TimeProvider timeProvider
)
{
    public const string StartupEventName = "apitally.app.startup";
    public const string ConsumerUpdateEventName = "apitally.consumer.update";
    public const string ValidationErrorEventName = "apitally.request.validation_error";
    public const string ServerErrorEventName = "apitally.request.server_error";

    public void EmitStartup(RuntimeConfiguration configuration, IReadOnlyList<EndpointPath> paths)
    {
        var versions = new JsonObject
        {
            ["dotnet"] = Environment.Version.ToString(),
            ["aspnetcore"] = GetInformationalVersion(typeof(HttpContext).Assembly),
        };
        if (configuration.AppVersion is not null)
            versions["app"] = configuration.AppVersion;
        var payload = new JsonObject
        {
            ["framework"] = "aspnetcore",
            ["versions"] = versions,
            ["config"] = SerializeConfiguration(configuration),
            ["paths"] = new JsonArray(paths.Select(SerializePath).ToArray()),
        };
        Emit(StartupEventName, payload.ToJsonString());
    }

    public void EmitConsumerUpdate(RequestConsumer consumer)
    {
        var body = new Dictionary<string, object?> { ["identifier"] = consumer.Identifier };
        if (consumer.Name is not null)
            body["name"] = consumer.Name;
        if (consumer.Group is not null)
            body["group"] = consumer.Group;
        if (consumer.Attributes.Count > 0)
            body["attributes"] = consumer.Attributes.ToDictionary(
                attribute => attribute.Key,
                attribute => (object?)attribute.Value
            );
        Emit(ConsumerUpdateEventName, body);
    }

    public void EmitErrorAggregates(ErrorAggregates errorAggregates)
    {
        var (validationErrors, serverErrors) = errorAggregates.Drain();
        foreach (var body in validationErrors)
            Emit(ValidationErrorEventName, body);
        foreach (var body in serverErrors)
            Emit(ServerErrorEventName, body);
    }

    private void Emit(string eventName, object body) =>
        logProcessor.OnEnd(
            LogSnapshot.ForInternalEvent(eventName, body, timeProvider.GetUtcNow().UtcDateTime)
        );

    // Credentials, endpoint, disabled flag, environment and app version are excluded.
    private static JsonObject SerializeConfiguration(RuntimeConfiguration configuration)
    {
        var config = new JsonObject
        {
            [nameof(ApitallyOptions.CaptureLogs)] = configuration.CaptureLogs,
            [nameof(ApitallyOptions.CaptureRequestHeaders)] = configuration.CaptureRequestHeaders,
            [nameof(ApitallyOptions.CaptureRequestBody)] = configuration.CaptureRequestBody,
            [nameof(ApitallyOptions.CaptureResponseHeaders)] = configuration.CaptureResponseHeaders,
            [nameof(ApitallyOptions.CaptureResponseBody)] = configuration.CaptureResponseBody,
            [nameof(ApitallyOptions.SampleRate)] = configuration.SampleRate,
            [nameof(ApitallyOptions.MaskQueryParams)] = SerializePatterns(
                configuration.MaskQueryParams
            ),
            [nameof(ApitallyOptions.MaskHeaders)] = SerializePatterns(configuration.MaskHeaders),
            [nameof(ApitallyOptions.MaskBodyFields)] = SerializePatterns(
                configuration.MaskBodyFields
            ),
            [nameof(ApitallyOptions.ExcludePaths)] = SerializePatterns(configuration.ExcludePaths),
        };
        var callbacks = new (string Name, Delegate? Callback)[]
        {
            (nameof(ApitallyOptions.SampleOnRequest), configuration.SampleOnRequest),
            (nameof(ApitallyOptions.SampleOnResponse), configuration.SampleOnResponse),
            (nameof(ApitallyOptions.MaskRequestBody), configuration.MaskRequestBody),
            (nameof(ApitallyOptions.MaskResponseBody), configuration.MaskResponseBody),
            (nameof(ApitallyOptions.MaskLogRecord), configuration.MaskLogRecord),
        };
        foreach (var (name, callback) in callbacks)
        {
            if (callback is not null)
                config[name] = true;
        }
        return config;
    }

    // User patterns are compiled case-insensitively; inline options in the pattern still apply.
    private static JsonArray SerializePatterns(IReadOnlyList<Regex> patterns) =>
        new(patterns.Select(pattern => (JsonNode)JsonValue.Create("(?i)" + pattern)).ToArray());

    private static JsonNode SerializePath(EndpointPath path)
    {
        var entry = new JsonObject { ["method"] = path.Method, ["path"] = path.Path };
        if (path.Summary is not null)
            entry["summary"] = path.Summary;
        if (path.Description is not null)
            entry["description"] = path.Description;
        return entry;
    }

    private static string GetInformationalVersion(Assembly assembly) =>
        assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? assembly.GetName().Version?.ToString()
        ?? "unknown";
}
