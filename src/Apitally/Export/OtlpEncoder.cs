using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Apitally.Logging;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Resources;
using OtlpResource = OpenTelemetry.Proto.Resource.V1.Resource;

namespace Apitally.Export;

internal static class OtlpEncoder
{
    public const string ScopeName = "apitally";
    public const string DistroName = "apitally-dotnet";
    public const int MaxRequestSize = 4_000_000;
    private const int RecordsPerRequest = 32;
    private const string InstanceIdKey = "service.instance.id";
    private const string EnvironmentKey = "deployment.environment.name";

    // Regenerated on every process start; the server treats a restart as a new instance.
    public static readonly string InstanceId = Guid.NewGuid().ToString();

    public static readonly string DistroVersion =
        typeof(OtlpEncoder)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    private static readonly ConditionalWeakTable<Resource, OtlpResource> OtlpResources = new();

    // Honors OTEL_SERVICE_NAME and OTEL_RESOURCE_ATTRIBUTES; the Apitally-owned keys always win.
    public static Resource CreateResource(string env) =>
        ResourceBuilder
            .CreateDefault()
            .AddAttributes(
                new Dictionary<string, object>
                {
                    [InstanceIdKey] = InstanceId,
                    [EnvironmentKey] = env,
                    ["telemetry.distro.name"] = DistroName,
                    ["telemetry.distro.version"] = DistroVersion,
                }
            )
            .Build();

    // Apitally copies of spans from an application-owned provider keep its resource but carry
    // Apitally's process identity and environment.
    public static Resource CreateExportResource(Resource applicationResource, string env) =>
        applicationResource.Merge(
            new Resource(
                new Dictionary<string, object>
                {
                    [InstanceIdKey] = InstanceId,
                    [EnvironmentKey] = env,
                }
            )
        );

    // Groups records into requests of at most MaxRequestSize bytes, measured on the complete
    // serialized request, and passes each request with that size. A record that cannot fit on
    // its own is dropped with a warning.
    public static void EncodeRequests<T>(
        IReadOnlyList<T> records,
        Func<IReadOnlyList<T>, IMessage> buildRequest,
        Action<IMessage, int> append,
        string signalName,
        SdkDiagnostics diagnostics,
        int recordsPerRequest = RecordsPerRequest
    )
    {
        for (var start = 0; start < records.Count; start += recordsPerRequest)
        {
            var count = Math.Min(recordsPerRequest, records.Count - start);
            EncodeChunk(
                new ArraySegment<T>([.. records.Skip(start).Take(count)]),
                buildRequest,
                append,
                signalName,
                diagnostics
            );
        }
    }

    public static ulong ToUnixNanoseconds(DateTime time) =>
        (ulong)(time.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) * 100;

    // Resources are immutable and generated messages cache nothing, so requests share one message.
    public static OtlpResource ToOtlpResource(Resource resource) =>
        OtlpResources.GetValue(
            resource,
            static resource =>
            {
                var output = new OtlpResource();
                foreach (var attribute in resource.Attributes)
                    output.Attributes.Add(ToKeyValue(attribute.Key, attribute.Value));
                return output;
            }
        );

    public static ByteString ToByteString(ActivityTraceId traceId)
    {
        Span<byte> bytes = stackalloc byte[16];
        traceId.CopyTo(bytes);
        return ByteString.CopyFrom(bytes);
    }

    public static ByteString ToByteString(ActivitySpanId spanId)
    {
        if (spanId == default)
            return ByteString.Empty;
        Span<byte> bytes = stackalloc byte[8];
        spanId.CopyTo(bytes);
        return ByteString.CopyFrom(bytes);
    }

    public static IEnumerable<KeyValue> ToKeyValues(
        IEnumerable<KeyValuePair<string, object?>> attributes
    ) => attributes.Select(attribute => ToKeyValue(attribute.Key, attribute.Value));

    public static KeyValue ToKeyValue(string key, object? value) =>
        new() { Key = key, Value = ToAnyValue(value) };

    // Values are already normalized by AttributeValues; null is an empty AnyValue.
    public static AnyValue ToAnyValue(object? value) =>
        value switch
        {
            null => new AnyValue(),
            string text => new AnyValue { StringValue = text },
            bool boolean => new AnyValue { BoolValue = boolean },
            long number => new AnyValue { IntValue = number },
            double number => new AnyValue { DoubleValue = number },
            byte[] bytes => new AnyValue { BytesValue = ByteString.CopyFrom(bytes) },
            IEnumerable<KeyValuePair<string, object?>> fields => new AnyValue
            {
                KvlistValue = new KeyValueList { Values = { ToKeyValues(fields) } },
            },
            Array items => new AnyValue
            {
                ArrayValue = new ArrayValue
                {
                    Values = { items.Cast<object?>().Select(ToAnyValue) },
                },
            },
            _ => ToAnyValue(
                AttributeValues.TryNormalize(value, out var normalized) ? normalized : null
            ),
        };

    private static void EncodeChunk<T>(
        ArraySegment<T> chunk,
        Func<IReadOnlyList<T>, IMessage> buildRequest,
        Action<IMessage, int> append,
        string signalName,
        SdkDiagnostics diagnostics
    )
    {
        var request = buildRequest(chunk);
        var size = request.CalculateSize();
        if (size <= MaxRequestSize)
        {
            append(request, size);
            return;
        }
        if (chunk.Count == 1)
        {
            diagnostics.OversizedRecordDropped(signalName, MaxRequestSize);
            return;
        }
        var half = chunk.Count / 2;
        EncodeChunk(chunk[..half], buildRequest, append, signalName, diagnostics);
        EncodeChunk(chunk[half..], buildRequest, append, signalName, diagnostics);
    }
}
