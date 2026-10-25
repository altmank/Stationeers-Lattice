using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Lattice.Core.Validation;

/// <summary>Where validation's heavy work runs: posted work runs in order, one piece at a time, off the main thread.</summary>
internal interface IValidationWorker
{
    /// <summary>Queues work; it runs after the work queued before it.</summary>
    void Post(Action work);

    /// <summary>Stops taking work and waits up to <paramref name="join"/> for the work running now to end.</summary>
    void Stop(TimeSpan join);
}

/// <summary>
/// Lattice's own worker thread for validation: one named background thread per world, started by the
/// constructor and stopped with the world. It never touches Unity or the game. Work it is given catches its own
/// exceptions; one that escapes ends the thread, and validation then sees no more answers.
/// </summary>
internal sealed class ThreadWorker : IValidationWorker
{
    /// <summary>How long ending the world waits for a run in progress.</summary>
    public static TimeSpan DefaultJoin => TimeSpan.FromSeconds(1);

    private readonly BlockingCollection<Action> _work = new();
    private readonly Thread _thread;

    public ThreadWorker()
        : this("Lattice validation")
    {
    }

    /// <summary>A worker thread with its own name ("Lattice analyzers" for the mods' worker analyzers).</summary>
    public ThreadWorker(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public void Post(Action work)
    {
        if (!_work.IsAddingCompleted)
            _work.Add(work);
    }

    public void Stop(TimeSpan join)
    {
        _work.CompleteAdding();
        _thread.Join(join);
    }

    private void Loop()
    {
        foreach (var work in _work.GetConsumingEnumerable())
            work();
    }
}
