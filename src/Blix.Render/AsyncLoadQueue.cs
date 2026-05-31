using System.Diagnostics;

namespace Blix.Render;

// Budgeted async load queue — the off-thread / CPU-parse twin of ResourceUploader
// (which budgets GPU upload). A producer runs off-thread; once it completes, its
// items are drained on the calling thread within a per-frame time budget, so a
// heavy parse doesn't stall a frame and the results trickle in.
//
//   load.Start(() => ParsePacks(...));     // heavy work, off-thread
//   // each frame, on the render thread:
//   if (load.Drain(budgetMs, StageOne)) { /* fully loaded — finish up */ }
//
// Generic over the item type; no graphics dependency. The "what to parse" is the
// caller's (a demo/game concern); this owns only the off-thread + budgeted-drain
// mechanism.
public sealed class AsyncLoadQueue<T>
{
    private Task<IReadOnlyList<T>>? producer;
    private readonly Queue<T> queue = new();
    private bool enqueued;

    // True while the producer is still running (nothing to drain yet).
    public bool IsProducing => producer is { IsCompleted: false };
    public bool IsFaulted => producer?.IsFaulted ?? false;
    public Exception? Fault => producer?.Exception?.GetBaseException();
    public int PendingCount => queue.Count;

    // Start the off-thread producer. Call once.
    public void Start(Func<IReadOnlyList<T>> produce)
    {
        ArgumentNullException.ThrowIfNull(produce);
        if (producer is not null)
        {
            throw new InvalidOperationException("AsyncLoadQueue already started.");
        }
        producer = Task.Run(produce);
    }

    // Drain up to `budgetMs` of items (always at least one once there is work),
    // running `process` on each. The producer's result is enqueued on the first
    // call after it completes. Returns true once fully loaded — producer done and
    // queue empty. While the producer is still running or has faulted, processes
    // nothing and returns false (inspect IsFaulted/Fault to handle a failure).
    public bool Drain(double budgetMs, Action<T> process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (producer is null || !producer.IsCompleted || producer.IsFaulted)
        {
            return false;
        }
        if (!enqueued)
        {
            foreach (var item in producer.Result) queue.Enqueue(item);
            enqueued = true;
        }
        if (queue.Count == 0) return true;

        var sw = Stopwatch.StartNew();
        do
        {
            process(queue.Dequeue());
        }
        while (queue.Count > 0 && sw.Elapsed.TotalMilliseconds < budgetMs);
        return queue.Count == 0;
    }
}
