namespace Apitally.Metrics;

// Delta exponential histogram for non-negative values at a fixed scale. Bucket counts cover only
// the recorded index range.
internal struct ExponentialHistogram
{
    public const int Scale = 3;

    private static readonly double ScalingFactor = (1 << Scale) / Math.Log(2);

    public long Count;
    public double Sum;
    public double Min;
    public double Max;
    public long ZeroCount;
    public int Offset;
    public int[]? Buckets;

    public void Record(double value)
    {
        Min = Count == 0 ? value : Math.Min(Min, value);
        Max = Count == 0 ? value : Math.Max(Max, value);
        Count++;
        Sum += value;
        if (value == 0)
        {
            ZeroCount++;
            return;
        }
        var index = MapToIndex(value);
        if (Buckets is null)
        {
            Buckets = new int[1];
            Offset = index;
        }
        else if (index < Offset || index >= Offset + Buckets.Length)
        {
            Grow(index);
        }
        Buckets[index - Offset]++;
    }

    // Exact powers of two are mapped exactly, as the OpenTelemetry specification requires.
    public static int MapToIndex(double value)
    {
        if (double.IsPow2(value))
            return (Math.ILogB(value) << Scale) - 1;
        return (int)Math.Ceiling(Math.Log(value) * ScalingFactor) - 1;
    }

    private void Grow(int index)
    {
        var low = Math.Min(Offset, index);
        var high = Math.Max(Offset + Buckets!.Length - 1, index);
        var grown = new int[high - low + 1];
        Buckets.CopyTo(grown, Offset - low);
        Buckets = grown;
        Offset = low;
    }
}
