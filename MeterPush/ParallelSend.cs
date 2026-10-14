// Sends the planned records several at a time (shared by setMonthly and setHourly).
// Setting: dbo.PushConfig GLOBAL SEND_PARALLELISM (default 100, 1 = one at a time).
static class ParallelSend
{
    public const int Default = 100;

    public static int Parallelism(Dictionary<string, string> global) =>
        int.TryParse(global.GetValueOrDefault("SEND_PARALLELISM"), out var n) ? Math.Clamp(n, 1, 200) : Default;

    // send runs on up to `parallelism` threads at once (the web request only). done runs one at a time, inside a lock,
    // right after a record's reply - use it for the database writes, counters and logging (the SQL connection is shared).
    // Stops starting new records after 5 failures in a row (in the order the replies arrive) or when ct is cancelled;
    // records already in flight (at most parallelism - 1) still finish and are logged. Returns why it stopped, or null.
    // Own threads are used (not the thread pool): the requests block, and the pool would only add a few threads at a time.
    public static string? Run<T>(List<T> items, int parallelism, CancellationToken ct,
        Func<T, (bool Ok, string Detail)> send, Action<T, bool, string> done)
    {
        var gate = new object();
        int consecutiveFails = 0, next = -1;
        string? stopped = null;

        void Worker()
        {
            try
            {
                while (true)
                {
                    var i = Interlocked.Increment(ref next);
                    if (i >= items.Count) return;
                    lock (gate)
                    {
                        if (stopped != null) return;
                        if (ct.IsCancellationRequested) { stopped = "stopped by the user"; return; }
                    }
                    var item = items[i];
                    var (ok, detail) = send(item);
                    lock (gate)
                    {
                        done(item, ok, detail);
                        if (ok) consecutiveFails = 0; else consecutiveFails++;
                        if (consecutiveFails >= 5 && stopped == null) stopped = "5 failures in a row";
                    }
                }
            }
            catch
            {
                lock (gate) stopped ??= "stopped by an error";
                throw; // reported once all workers have finished
            }
        }

        var workers = Enumerable.Range(0, Math.Max(1, Math.Min(parallelism, items.Count)))
            .Select(_ => Task.Factory.StartNew(Worker, TaskCreationOptions.LongRunning)).ToArray();
        Task.WaitAll(workers);
        return stopped;
    }
}
