using System.Collections;
using System.Globalization;

namespace Apitally.Export;

// Converts CLR attribute values to the types the official OTel .NET OTLP exporter writes,
// detached from application-owned objects.
internal static class AttributeValues
{
    public static Dictionary<string, object?> Normalize(
        IEnumerable<KeyValuePair<string, object?>> attributes
    )
    {
        var normalized = new Dictionary<string, object?>();
        foreach (var (key, value) in attributes)
        {
            if (TryNormalize(value, out var converted))
                normalized[key] = converted;
            else
                normalized.Remove(key);
        }
        return normalized;
    }

    public static bool TryNormalize(object? value, out object? normalized)
    {
        try
        {
            normalized = value switch
            {
                IDictionary dictionary => NormalizeDictionary(dictionary),
                Array array => NormalizeArray(array),
                _ => NormalizeScalar(value),
            };
            return true;
        }
        catch
        {
            normalized = null;
            return false;
        }
    }

    private static object? NormalizeScalar(object? value) =>
        value switch
        {
            null => null,
            string or bool or long or double => value,
            int number => (long)number,
            short number => (long)number,
            sbyte number => (long)number,
            byte number => (long)number,
            uint number => (long)number,
            ushort number => (long)number,
            float number => (double)number,
            _ => ToInvariantString(value),
        };

    private static object NormalizeArray(Array array) =>
        array switch
        {
            byte[] bytes => bytes.ToArray(),
            string[] strings => strings.ToArray(),
            bool[] booleans => booleans.ToArray(),
            long[] or int[] or short[] or sbyte[] or uint[] or ushort[] => array
                .Cast<object>()
                .Select(Convert.ToInt64)
                .ToArray(),
            double[] or float[] => array.Cast<object>().Select(Convert.ToDouble).ToArray(),
            _ => array.Cast<object?>().Select(ToInvariantString).ToArray(),
        };

    // Maps convert one level deep; nested maps use the string fallback.
    private static Dictionary<string, object?> NormalizeDictionary(IDictionary dictionary)
    {
        var normalized = new Dictionary<string, object?>();
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = ToInvariantString(entry.Key) ?? "";
            normalized[key] = entry.Value switch
            {
                IDictionary nested => ToInvariantString(nested),
                Array array => NormalizeArray(array),
                var value => NormalizeScalar(value),
            };
        }
        return normalized;
    }

    private static string? ToInvariantString(object? value) =>
        value is null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
}
