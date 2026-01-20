namespace Apitally.Tests;

using System.Text.Json;

internal static class TestHelpers
{
    public static JsonElement[] GetLoggedItems(RequestLogger requestLogger)
    {
        requestLogger.Maintain();
        requestLogger.RotateFile();

        var logFile = requestLogger.GetFile();
        if (logFile == null)
        {
            return Array.Empty<JsonElement>();
        }

        var lines = logFile.ReadDecompressedLines();
        var items = new JsonElement[lines.Count];

        for (int i = 0; i < lines.Count; i++)
        {
            items[i] = JsonDocument.Parse(lines[i]).RootElement;
        }

        logFile.Delete();
        return items;
    }
}
