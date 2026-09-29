using System.Runtime.CompilerServices;

namespace Apitally.Requests;

// The OTel exception.stacktrace value: the exception's full string representation, including
// type, message and inner exceptions. Formatted once and shared by all call sites.
internal static class ExceptionStacktrace
{
    private static readonly ConditionalWeakTable<Exception, string> Formatted = new();

    public static string Get(Exception exception) =>
        Formatted.GetValue(exception, static exception => exception.ToString());
}
