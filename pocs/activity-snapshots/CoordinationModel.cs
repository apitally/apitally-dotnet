// Single-request sequential model. No ASP.NET completion ordering or concurrent safety is claimed.
internal sealed class CoordinationModel(int spanCap, int logCap)
{
    private readonly List<string> descendants = [];
    private readonly List<string> logs = [];
    private bool transportComplete;
    private bool serverComplete;
    private bool released;
    private bool dropped;

    internal List<string> Output { get; } = [];
    internal byte[]? RawBody { get; set; }
    internal int ReleaseCount { get; private set; }
    internal int ResponseDecisionCount { get; private set; }
    internal int BufferedCount => descendants.Count + logs.Count;

    internal void Descendant(string name) => Add(descendants, "span:" + name, spanCap);

    internal void Log(string name) => Add(logs, "log:" + name, logCap);

    internal void CompleteTransport(bool keep)
    {
        if (transportComplete || dropped)
        {
            return;
        }
        transportComplete = true;
        ResponseDecisionCount++;
        if (!keep)
        {
            dropped = true;
            descendants.Clear();
            logs.Clear();
            RawBody = null;
            return;
        }
        ReleaseIfComplete();
    }

    internal void CompleteServer()
    {
        serverComplete = true;
        ReleaseIfComplete();
    }

    private void Add(List<string> buffer, string name, int cap)
    {
        if (dropped)
        {
            return;
        }
        if (released)
        {
            Output.Add(name);
        }
        else if (buffer.Count < cap)
        {
            buffer.Add(name);
        }
    }

    private void ReleaseIfComplete()
    {
        if (dropped || released || !transportComplete || !serverComplete)
        {
            return;
        }
        released = true;
        ReleaseCount++;
        Output.AddRange(descendants);
        Output.Add("span:SERVER");
        Output.AddRange(logs);
        descendants.Clear();
        logs.Clear();
        RawBody = null;
    }
}
