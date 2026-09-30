using System.Diagnostics;

namespace Apitally;

/// <summary>
/// Request helpers for the current HTTP request. Inject this service where needed. Outside a
/// monitored request, or when Apitally is disabled, the methods do nothing.
/// </summary>
public interface IApitally
{
    /// <summary>
    /// Identifies the consumer of the current request. <paramref name="attributes"/> is a
    /// partial update of the consumer's custom attributes; a null value deletes an attribute.
    /// </summary>
    void SetConsumer(
        string identifier,
        string? name = null,
        string? group = null,
        IReadOnlyDictionary<string, string?>? attributes = null
    );

    /// <summary>Sets an attribute on the request's server span.</summary>
    void SetRequestAttribute(string key, object? value);

    /// <summary>
    /// Captures an exception for the current request. Only the first captured exception is kept.
    /// </summary>
    void CaptureException(Exception exception);

    /// <summary>
    /// Starts an internal child activity for manual tracing. Dispose it to end the span.
    /// </summary>
    /// <returns>
    /// The started activity, or <c>null</c> when no tracer provider listens, for example when
    /// Apitally is disabled. The activity is exported to Apitally only when it is recorded within
    /// a monitored request.
    /// </returns>
    Activity? StartActivity(string name);
}
