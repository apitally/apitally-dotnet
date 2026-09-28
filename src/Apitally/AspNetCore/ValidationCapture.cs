using System.Text.Json;
using Apitally.Export;
using Apitally.Requests;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;

namespace Apitally.AspNetCore;

// One normalized validation error. The field is an opaque string supplied by the framework.
internal sealed record ValidationDetail(string Source, string Field, string Message, string Type);

// Recognizes framework validation responses: typed details from MVC and problem-details
// callbacks, else known standard response shapes. Unknown formats are skipped.
internal static class ValidationCapture
{
    private const string ValidationTitle = "One or more validation errors occurred.";

    // Registering options callbacks does not install MVC in Minimal-only applications.
    public static void Register(IServiceCollection services)
    {
        services.PostConfigure<ApiBehaviorOptions>(options =>
        {
            var original = options.InvalidModelStateResponseFactory;
            options.InvalidModelStateResponseFactory = context =>
            {
                CaptureModelState(context);
                return original(context);
            };
        });
        services.PostConfigure<ProblemDetailsOptions>(options =>
        {
            var original = options.CustomizeProblemDetails;
            options.CustomizeProblemDetails = context =>
            {
                original?.Invoke(context);
                if (context.ProblemDetails is HttpValidationProblemDetails validation)
                    RequestRegistry
                        .Get(context.HttpContext)
                        ?.AddValidationDetails(ToDetails(validation.Errors));
            };
        });
    }

    // Validation responses are retained for parsing even when body capture is off.
    public static bool IsValidationResponse(int statusCode, string? contentType)
    {
        if (statusCode is not (400 or 422))
            return false;
        var mediaType = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        return mediaType is "application/json" or "application/problem+json";
    }

    public static List<ValidationDetail> ParseResponse(
        int statusCode,
        string? contentType,
        string? contentEncoding,
        byte[] bytes
    )
    {
        if (!IsValidationResponse(statusCode, contentType))
            return [];
        var decoded = SpanRedaction.Decompress(bytes, contentEncoding, out var failure);
        if (failure is not null)
            return [];
        try
        {
            using var document = JsonDocument.Parse(decoded);
            return ParseKnownShape(document.RootElement, statusCode, contentType!);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void CaptureModelState(ActionContext context)
    {
        if (RequestRegistry.Get(context.HttpContext) is not { } state)
            return;
        var parameters = context.ActionDescriptor.Parameters;
        var details = new List<ValidationDetail>();
        foreach (var (key, entry) in context.ModelState)
        {
            if (entry is null)
                continue;
            var source = GetBindingSource(parameters, key);
            foreach (var error in entry.Errors)
                details.Add(
                    new ValidationDetail(
                        source,
                        key,
                        error.ErrorMessage.Length > 0
                            ? error.ErrorMessage
                            : error.Exception?.Message ?? "",
                        error.Exception?.GetType().FullName ?? ""
                    )
                );
        }
        state.AddValidationDetails(details);
    }

    // Only an exact binding-name match or a single body parameter identifies the source.
    private static string GetBindingSource(
        IList<Microsoft.AspNetCore.Mvc.Abstractions.ParameterDescriptor> parameters,
        string key
    )
    {
        var bindingSource = parameters
            .FirstOrDefault(parameter =>
                (parameter.BindingInfo?.BinderModelName ?? parameter.Name) == key
            )
            ?.BindingInfo?.BindingSource;
        if (
            bindingSource is null
            && parameters.Count == 1
            && parameters[0].BindingInfo?.BindingSource == BindingSource.Body
        )
            bindingSource = BindingSource.Body;
        return bindingSource?.Id.ToLowerInvariant() switch
        {
            "body" => "body",
            "query" => "query",
            "path" => "path",
            "header" => "header",
            _ => "",
        };
    }

    // The standard problem document is identified by its type and status fields; the compact
    // .NET 10 Minimal API form by its default title and absent type and status.
    private static List<ValidationDetail> ParseKnownShape(
        JsonElement root,
        int statusCode,
        string contentType
    )
    {
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("title", out var title)
            || title.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("errors", out var errors)
            || errors.ValueKind != JsonValueKind.Object
        )
            return [];
        var isProblemJson = contentType.Contains(
            "application/problem+json",
            StringComparison.OrdinalIgnoreCase
        );
        var hasStatus = root.TryGetProperty("status", out var status);
        var hasType = root.TryGetProperty("type", out var type);
        var isStandard =
            isProblemJson
            && hasStatus
            && status.ValueKind == JsonValueKind.Number
            && status.TryGetInt32(out var statusValue)
            && statusValue == statusCode
            && hasType
            && type.ValueKind == JsonValueKind.String
            && type.GetString()
                == (
                    statusCode == 400
                        ? "https://tools.ietf.org/html/rfc9110#section-15.5.1"
                        : "https://tools.ietf.org/html/rfc4918#section-11.2"
                );
        var isCompact =
            statusCode == 400
            && !isProblemJson
            && !hasStatus
            && !hasType
            && title.GetString() == ValidationTitle;
        if (!isStandard && !isCompact)
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
                details.Add(new ValidationDetail("", property.Name, message.GetString()!, ""));
            }
        }
        return details;
    }

    private static IEnumerable<ValidationDetail> ToDetails(IDictionary<string, string[]> errors) =>
        errors.SelectMany(pair =>
            pair.Value.Select(message => new ValidationDetail("", pair.Key, message, ""))
        );
}
