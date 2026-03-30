namespace Apitally;

using System.Collections.Generic;
using System.Linq;
using Apitally.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;

static class ApitallyUtils
{
    public static List<Path> GetPaths(IEnumerable<EndpointDataSource> endpointSources)
    {
        return
        [
            .. endpointSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(static endpoint =>
                    endpoint.Metadata.GetMetadata<HttpMethodMetadata>() != null
                    && endpoint.RoutePattern.RawText != null
                )
                .SelectMany(static endpoint =>
                    endpoint
                        .Metadata.GetMetadata<HttpMethodMetadata>()!
                        .HttpMethods.Select(method => new Path
                        {
                            Method = method,
                            PathValue = endpoint.RoutePattern.RawText!.StartsWith('/')
                                ? endpoint.RoutePattern.RawText
                                : $"/{endpoint.RoutePattern.RawText}",
                        })
                )
                .Where(path =>
                    !string.IsNullOrEmpty(path.PathValue)
                    && path.Method != "OPTIONS"
                    && path.Method != "HEAD"
                ),
        ];
    }

    public static string NormalizePath(string path)
    {
        return path.StartsWith('/') ? path : $"/{path}";
    }

    internal static string GetRequestUrl(HttpRequest request)
    {
        var url = request.GetDisplayUrl();
        if (
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && IsHttps(request.Headers)
        )
        {
            url = string.Concat("https://", url.AsSpan(7));
        }
        return url;
    }

    internal static bool IsHttps(IHeaderDictionary headers)
    {
        string[] schemeHeaders =
        [
            "X-Forwarded-Proto",
            "X-Forwarded-Protocol",
            "X-Forwarded-Scheme",
            "X-Url-Scheme",
            "X-Scheme",
        ];
        foreach (var key in schemeHeaders)
        {
            var value = headers[key].FirstOrDefault();
            if (value is not null)
            {
                var scheme = value.Split(',', 2)[0].Trim();
                if (scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        var forwarded = headers["Forwarded"].FirstOrDefault();
        if (forwarded is not null)
        {
            foreach (var param in forwarded.Split([',', ';']))
            {
                var trimmed = param.Trim();
                if (trimmed.StartsWith("proto=", StringComparison.OrdinalIgnoreCase))
                {
                    var v = trimmed[6..].Trim().Trim('"');
                    if (v.Equals("https", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        if (
            string.Equals(
                headers["Front-End-Https"].FirstOrDefault(),
                "on",
                StringComparison.OrdinalIgnoreCase
            )
            || string.Equals(
                headers["X-Forwarded-Ssl"].FirstOrDefault(),
                "on",
                StringComparison.OrdinalIgnoreCase
            )
        )
            return true;

        return false;
    }

    public static Dictionary<string, string> GetVersions() =>
        new()
        {
            { "dotnet", Environment.Version.ToString() },
            {
                "aspnetcore",
                typeof(ActionDescriptor).Assembly.GetName().Version?.ToString() ?? "unknown"
            },
            {
                "apitally",
                typeof(ApitallyUtils).Assembly.GetName().Version?.ToString() ?? "unknown"
            },
        };
}
