namespace Apitally;

using System.Collections.Generic;
using System.Linq;
using Apitally.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;

public static class ApitallyUtils
{
    public static List<Path> GetPaths(
        IActionDescriptorCollectionProvider actionDescriptorCollectionProvider,
        IEnumerable<EndpointDataSource> endpointSources
    )
    {
        var paths = new List<Path>();

        // Get paths from MVC controllers
        paths.AddRange(
            actionDescriptorCollectionProvider
                .ActionDescriptors.Items.SelectMany(descriptor =>
                    descriptor
                        .ActionConstraints?.OfType<HttpMethodActionConstraint>()
                        .SelectMany(constraint =>
                            constraint.HttpMethods.Select(method => new Path
                            {
                                Method = method,
                                PathValue = descriptor.AttributeRouteInfo?.Template ?? string.Empty,
                            })
                        ) ?? Enumerable.Empty<Path>()
                )
                .Where(path => !string.IsNullOrEmpty(path.PathValue))
        );

        // Get paths from minimal APIs
        paths.AddRange(
            endpointSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Select(endpoint => new Path
                {
                    Method =
                        endpoint
                            .Metadata.GetMetadata<HttpMethodMetadata>()
                            ?.HttpMethods.FirstOrDefault() ?? "GET",
                    PathValue = endpoint.RoutePattern.RawText ?? string.Empty,
                })
                .Where(path => !string.IsNullOrEmpty(path.PathValue))
        );

        return paths;
    }

    public static Dictionary<string, string> GetVersions() =>
        new()
        {
            { "dotnet", Environment.Version.ToString() },
            {
                "aspnetcore",
                typeof(ActionDescriptor).Assembly.GetName().Version?.ToString() ?? "unknown"
            },
        };
}
