using System.Collections.Concurrent;

namespace TradingCsvProcessor.Infrastructure;

// Singleton. Holds one CancellationTokenSource per actively-running job.
// The API layer calls TryCancel(); the worker calls Register()/Unregister().
public sealed class JobCancellationRegistry
{
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _map = new();

    // Called by the worker when it picks up a job. Returns the job-scoped token.
    public CancellationToken Register(Guid jobId)
    {
        var cts = new CancellationTokenSource();
        _map[jobId] = cts;
        return cts.Token;
    }

    // Called by the cancel API endpoint.
    // Returns true  → in-memory signal delivered (job is currently being processed).
    // Returns false → job is not in the registry (queued/completed — DB flag handles it).
    public bool TryCancel(Guid jobId)
    {
        if (_map.TryGetValue(jobId, out var cts) && !cts.IsCancellationRequested)
        {
            cts.Cancel();
            return true;
        }
        return false;
    }

    public bool IsRunning(Guid jobId) => _map.ContainsKey(jobId);

    // Called in the worker's finally block — regardless of how processing ended.
    public void Unregister(Guid jobId)
    {
        if (_map.TryRemove(jobId, out var cts))
            cts.Dispose();
    }
}
