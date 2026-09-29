using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Apitally.AspNetCore;

internal sealed record EndpointPath(
    string Method,
    string Path,
    string? Summary,
    string? Description
);

internal static class EndpointMetadata
{
    // Finalized route/method pairs with native summaries and descriptions, without OpenAPI.
    public static List<EndpointPath> GetPaths(IServiceProvider services)
    {
        var dataSource = services.GetService<EndpointDataSource>();
        if (dataSource is null)
            return [];
        var paths = new List<EndpointPath>();
        var seen = new HashSet<(string, string)>();
        foreach (var endpoint in dataSource.Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
            if (methods is null || endpoint.RoutePattern.RawText is null)
                continue;
            var path = NormalizeRoute(endpoint.RoutePattern.RawText);
            foreach (var method in methods.Select(method => method.ToUpperInvariant()))
            {
                if (method is "HEAD" or "OPTIONS" || !seen.Add((method, path)))
                    continue;
                paths.Add(
                    new EndpointPath(
                        method,
                        path,
                        endpoint.Metadata.GetMetadata<IEndpointSummaryMetadata>()?.Summary,
                        endpoint.Metadata.GetMetadata<IEndpointDescriptionMetadata>()?.Description
                    )
                );
            }
        }
        return paths;
    }

    // Exception-handler re-execution exposes the originally matched endpoint, which is null for
    // exceptions thrown before routing. Handlers that set the feature without endpoint or route
    // values, such as Hellang ProblemDetails, keep the matched endpoint on the context.
    // Unmatched requests have no route. Routes exclude the path base, matching GetPaths.
    public static string? ResolveRoute(HttpContext context)
    {
        var endpoint = context.Features.Get<IExceptionHandlerFeature>() is { } handled
            ? handled.Endpoint ?? (handled.RouteValues is null ? context.GetEndpoint() : null)
            : context.GetEndpoint();
        return endpoint is RouteEndpoint { RoutePattern.RawText: { } route }
            ? NormalizeRoute(route)
            : null;
    }

    // A group's root endpoint has the raw text "/group/", which matches the same paths as "/group".
    public static string NormalizeRoute(string route) => "/" + route.Trim('/');
}
