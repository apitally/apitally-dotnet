namespace Apitally;

using System.Collections.Generic;
using System.Linq;
using Apitally.Models;
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
                .Where(path => !string.IsNullOrEmpty(path.PathValue)),
        ];
    }

    public static string NormalizePath(string path)
    {
        return path.StartsWith('/') ? path : $"/{path}";
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
                typeof(ApitallyUtils).Assembly.GetName().Version?.ToString(3) ?? "unknown"
            },
        };
}
