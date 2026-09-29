using Apitally.Export;

namespace Apitally.AspNetCore;

// Byte counts and bounded capture for one body direction. Eligibility is decided from headers,
// and counts continue independently of capture. While eligibility is undecided (null), bytes are
// retained provisionally and the decision is requested again on the next use.
internal sealed class BodyCapture(Func<bool?> isEligible, Func<long?> declaredLength)
{
    public const int MaxBodySize = SpanRedaction.MaxBodySize;
    private const int InitialBufferSize = 4_096;

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
    // only counts once Commit confirms the operation was accepted. The buffer starts at the
    // declared length when known and doubles as needed. A declared oversized body is never staged.
    public int Stage(ReadOnlySpan<byte> bytes)
    {
        if (!IsCapturing() || bytes.Length > MaxBodySize - Count || declaredLength() > MaxBodySize)
            return 0;
        var required = used + bytes.Length;
        if (buffer is null || buffer.Length < required)
        {
            var size = buffer is null ? declaredLength() ?? InitialBufferSize : buffer.Length * 2;
            Array.Resize(ref buffer, (int)Math.Clamp(size, required, MaxBodySize));
        }
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
            IsTooLarge = eligible == true;
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
        if (IsBypassed || (!IsCapturing() && !IsTooLarge))
            return null;
        if (IsTooLarge)
            return CapturedBody.TooLarge;
        if (!isComplete || IsIncomplete || used == 0 || used != Count)
            return null;
        return new CapturedBody(GetBytes(), contentEncoding);
    }

    // Returns whatever complete bytes are retained, independent of export eligibility.
    public byte[]? GetRetainedBytes() =>
        IsCapturing() && !IsIncomplete && !IsBypassed && used > 0 && used == Count
            ? GetBytes()
            : null;

    // Callers share the buffer once it is trimmed; a later write reallocates rather than mutates.
    private byte[] GetBytes()
    {
        if (buffer!.Length != used)
            buffer = buffer[..used];
        return buffer;
    }

    private bool IsCapturing()
    {
        if (eligible is null)
        {
            eligible = isEligible();
            if (eligible is null)
                return !IsIncomplete && !IsBypassed;
            // An oversized declared or already counted body yields the marker without its bytes.
            if (eligible.Value && (declaredLength() > MaxBodySize || Count > MaxBodySize))
                IsTooLarge = true;
            if (!eligible.Value || IsTooLarge)
            {
                buffer = null;
                used = 0;
            }
        }
        return eligible.Value && !IsTooLarge && !IsIncomplete && !IsBypassed;
    }
}
