namespace Apitally;

using System.ComponentModel.DataAnnotations;
using Apitally.Models;

public class ApitallyOptions
{
    [Required]
    [RegularExpression(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        ErrorMessage = "Client ID must be a valid UUID"
    )]
    public string ClientId { get; set; } = string.Empty;

    [RegularExpression(
        @"^[\w-]{1,32}$",
        ErrorMessage = "Env must be 1-32 characters long and contain only word characters and hyphens"
    )]
    public string Env { get; set; } = "default";

    public RequestLoggingOptions RequestLogging { get; set; } = new RequestLoggingOptions();
}

public class RequestLoggingOptions
{
    public bool Enabled { get; set; } = false;
    public bool IncludeQueryParams { get; set; } = true;
    public bool IncludeRequestHeaders { get; set; } = false;
    public bool IncludeRequestBody { get; set; } = false;
    public bool IncludeResponseHeaders { get; set; } = true;
    public bool IncludeResponseBody { get; set; } = false;
    public bool IncludeException { get; set; } = true;
    public bool CaptureLogs { get; set; } = false;
    public List<string> QueryParamMaskPatterns { get; set; } = [];
    public List<string> HeaderMaskPatterns { get; set; } = [];
    public List<string> BodyFieldMaskPatterns { get; set; } = [];
    public List<string> PathExcludePatterns { get; set; } = [];

    /// <summary>
    /// Function to mask sensitive data in the request body.
    /// Return null to mask the whole body.
    /// </summary>
    public Func<Request, byte[]?> MaskRequestBody { get; set; } = request => request.Body;

    /// <summary>
    /// Function to mask sensitive data in the response body.
    /// Return null to mask the whole body.
    /// </summary>
    public Func<Request, Response, byte[]?> MaskResponseBody { get; set; } =
        (request, response) => response.Body;

    /// <summary>
    /// Function to determine whether a request should be excluded from logging.
    /// Return true to exclude the request.
    /// </summary>
    public Func<Request, Response, bool> ShouldExclude { get; set; } = (request, response) => false;
}
