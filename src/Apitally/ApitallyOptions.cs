namespace Apitally;

using System.ComponentModel.DataAnnotations;

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
    public List<string> QueryParamMaskPatterns { get; set; } = new();
    public List<string> HeaderMaskPatterns { get; set; } = new();
    public List<string> PathExcludePatterns { get; set; } = new();
}
