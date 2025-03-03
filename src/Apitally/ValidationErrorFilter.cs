namespace Apitally;

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

public class ValidationErrorFilter(ILogger<ValidationErrorFilter> logger)
    : IActionFilter,
        IOrderedFilter
{
    public int Order => int.MinValue + 100;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        try
        {
            if (!context.ModelState.IsValid && context.ModelState.ErrorCount > 0)
            {
                var validationErrors = context
                    .ModelState.Where(x => x.Value?.Errors.Count > 0)
                    .SelectMany(kvp =>
                        kvp.Value!.Errors.Select(error => new ValidationError
                        {
                            Location = kvp.Key.Split('.', StringSplitOptions.RemoveEmptyEntries),
                            Message = error.ErrorMessage,
                            Type = error.Exception?.GetType().Name ?? "",
                        })
                    )
                    .ToList();
                context.HttpContext.Items["ApitallyValidationErrors"] = validationErrors;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error capturing validation errors");
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}

public class ValidationError
{
    public string[] Location { get; set; } = [];
    public string Message { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}
