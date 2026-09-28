using Apitally.Export;

namespace Apitally.AspNetCore;

// Byte counts and bounded capture for one body direction. Eligibility is decided from headers
// before any bytes are retained, and counts continue independently of capture.
internal sealed class BodyCapture(Func<bool> isEligible, Func<long?> declaredLength)
{
    public const int MaxBodySize = SpanRedaction.MaxBodySize;

    private static readonly string[] AllowedContentTypes =
    [
        "application/json",
        "application/problem+json",
        "application/vnd.api+json",
        "application/ld+json",
        "application/x-ndjson",
        "text/markdown",
        "text/plain",
    ];

    private bool? eligible;
    private byte[]? buffer;
    private int used;

    public long Count { get; private set; }
    public bool IsEndOfStream { get; set; }
    public bool IsIncomplete { get; private set; }
    public bool IsTooLarge { get; private set; }

    // Set when a native file send bypassed observation.
    public bool IsBypassed { get; private set; }
    public bool HasUnknownLength { get; private set; }

    public static bool IsAllowedContentType(string? contentType) =>
        contentType is not null
        && AllowedContentTypes.Any(allowed =>
            contentType.TrimStart().StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
        );

    public static bool IsSupportedContentEncoding(string? contentEncoding) =>
        contentEncoding?.Trim().ToLowerInvariant()
            is null
                or ""
                or "identity"
                or "gzip"
                or "deflate"
                or "br";

    // Copies bytes into the bounded buffer while caller-owned memory is still valid. The copy
    // only counts once Commit confirms the operation was accepted.
    public int Stage(ReadOnlySpan<byte> bytes)
    {
        if (!IsCapturing() || bytes.Length > MaxBodySize - used)
            return 0;
        buffer ??= new byte[MaxBodySize];
        bytes.CopyTo(buffer.AsSpan(used));
        return bytes.Length;
    }

    public void Commit(long count, int staged)
    {
        Count += count;
        if (!IsCapturing())
            return;
        if (Count > MaxBodySize)
        {
            // A body is never exported truncated.
            IsTooLarge = true;
            buffer = null;
            used = 0;
            return;
        }
        used += staged;
    }

    public void Observe(ReadOnlySpan<byte> bytes) => Commit(bytes.Length, Stage(bytes));

    public void MarkIncomplete()
    {
        IsIncomplete = true;
        buffer = null;
        used = 0;
    }

    // Native file sends are delegated unchanged; their bytes are never captured.
    public void Bypass(long? count)
    {
        IsBypassed = true;
        buffer = null;
        used = 0;
        if (count is { } length)
            Count += length;
        else
            HasUnknownLength = true;
    }

    // The oversized marker is exported even for an incomplete stream; partial bytes never are.
    public CapturedBody? GetBody(bool isComplete, string? contentEncoding)
    {
        if (!IsCapturing() && !IsTooLarge)
            return null;
        if (IsTooLarge)
            return CapturedBody.TooLarge;
        if (!isComplete || IsIncomplete || IsBypassed || used == 0 || used != Count)
            return null;
        return new CapturedBody(buffer![..used], contentEncoding);
    }

    // Returns whatever complete bytes are retained, independent of export eligibility.
    public byte[]? GetRetainedBytes(bool isComplete) =>
        isComplete && IsCapturing() && !IsIncomplete && !IsBypassed && used > 0 && used == Count
            ? buffer![..used]
            : null;

    private bool IsCapturing()
    {
        if (eligible is null)
        {
            eligible = isEligible();
            // A declared oversized body yields the marker without retaining a byte.
            if (eligible.Value && declaredLength() > MaxBodySize)
                IsTooLarge = true;
        }
        return eligible.Value && !IsTooLarge && !IsIncomplete && !IsBypassed;
    }
}
