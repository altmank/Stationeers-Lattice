using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Lattice.Api;

namespace Lattice.Core.Validation;

/// <summary>
/// Calls mods' worker rules on a rule thread of Lattice's own, so the time limit holds for a rule that never returns
/// (the <see cref="RuleDescriptor"/> contract). The caller waits for the call until the limit plus
/// <see cref="StuckAfter"/>; the call is timed on the rule thread, and one past the limit is late (its answer is
/// dropped). A call still running at the deadline is abandoned with its thread: that rule is not called again until
/// the call returns, and the next call starts a fresh rule thread. Rule threads are background threads named
/// "Lattice rules"; ending the world closes the current one and joins none. A stuck thread's rule may read a world
/// that moves on; what it does there is its own, and whatever it answers is dropped.
/// </summary>
internal sealed class RuleThreads : IRuleCalls, IDisposable
{
    /// <summary>How long past the limit a call may run before its thread counts as stuck.</summary>
    public static TimeSpan DefaultStuckAfter => TimeSpan.FromMilliseconds(50);

    private readonly object _gate = new();
    private readonly HashSet<RuleId> _inside = [];
    private RuleThread? _current;
    private int _threadCount;
    private bool _ended;

    public RuleThreads(TimeSpan stuckAfter) => StuckAfter = stuckAfter;

    public RuleThreads()
        : this(DefaultStuckAfter)
    {
    }

    public TimeSpan StuckAfter { get; }

    /// <summary>How many rule threads were started (a stuck one is replaced).</summary>
    public int ThreadsStarted
    {
        get
        {
            lock (_gate)
                return _threadCount;
        }
    }

    public CallResult<T> Call<T>(RuleId rule, TimeSpan limit, Func<T> call)
    {
        RuleThread ruleThread;
        lock (_gate)
        {
            if (_ended || _inside.Contains(rule))
                return new CallResult<T>.Late();
            ruleThread = _current ??= new RuleThread(++_threadCount);
        }

        var job = new Job<T>(call);
        if (!ruleThread.Post(job))
            return new CallResult<T>.Late();
        if (!job.Done.Wait(limit + StuckAfter))
        {
            lock (_gate)
            {
                if (job.TryAbandon(() => Release(rule)))
                {
                    _inside.Add(rule);
                    if (_current == ruleThread)
                        _current = null;
                    ruleThread.Close();
                    return new CallResult<T>.Late();
                }
            }

            job.Done.Wait();
        }

        job.Done.Dispose();
        return job.Result(limit);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _ended = true;
            _current?.Close();
            _current = null;
        }
    }

    private void Release(RuleId rule)
    {
        lock (_gate)
            _inside.Remove(rule);
    }

    private abstract class RuleJob
    {
        public abstract void Run();
    }

    private sealed class Job<T>(Func<T> call) : RuleJob
    {
        private readonly object _state = new();
        private T _value = default!;
        private Exception? _error;
        private TimeSpan _took;
        private bool _finished;
        private Action? _release;

        public ManualResetEventSlim Done { get; } = new(false);

        public override void Run()
        {
            var watch = Stopwatch.StartNew();
            try
            {
                _value = call();
            }
            catch (Exception error)
            {
                _error = error;
            }

            _took = watch.Elapsed;
            Action? release;
            lock (_state)
            {
                _finished = true;
                release = _release;
            }

            if (release is null)
                Done.Set();
            else
                release();
        }

        /// <summary>Gives the call up unless it has finished; <paramref name="release"/> runs when it does finish.</summary>
        public bool TryAbandon(Action release)
        {
            lock (_state)
            {
                if (_finished)
                    return false;
                _release = release;
                return true;
            }
        }

        public CallResult<T> Result(TimeSpan limit)
        {
            if (_error is not null)
                return new CallResult<T>.Threw(_error);
            return _took > limit ? new CallResult<T>.Late() : new CallResult<T>.Returned(_value);
        }
    }

    /// <summary>One rule thread and its queue; closed, it ends after the call it is in.</summary>
    private sealed class RuleThread
    {
        private readonly BlockingCollection<RuleJob> _jobs = new();

        public RuleThread(int number)
        {
            var thread = new Thread(Loop) { IsBackground = true, Name = "Lattice rules " + number };
            thread.Start();
        }

        /// <summary>False when the rule thread closed meanwhile (the world ended).</summary>
        public bool Post(RuleJob job)
        {
            try
            {
                return _jobs.TryAdd(job);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Close() => _jobs.CompleteAdding();

        private void Loop()
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
                job.Run();
        }
    }
}
