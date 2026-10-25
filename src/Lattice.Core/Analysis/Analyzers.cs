using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Analysis;

/// <summary>
/// The analyzers of one world that mods register (<see cref="IRegistries.Register(AnalyzerDescriptor)"/>), in
/// registration order, from a copied array: a registration or disposal during a call takes effect from the next
/// preview or analysis. Registrations end with the world. Calls are plain: no catch, no timing, no
/// switching off; an analyzer's exception leaves the preview or analysis that called it. Registration is on the main
/// thread; the worker reads <see cref="Current"/>.
/// </summary>
internal sealed class Analyzers
{
    private volatile AnalyzerDescriptor[] _analyzers = [];

    /// <summary>The analyzers now registered, in registration order.</summary>
    public IReadOnlyList<AnalyzerDescriptor> Current => _analyzers;

    /// <summary>Whether any analyzer runs on the thread.</summary>
    public bool Any(CallbackThread thread)
    {
        foreach (var analyzer in _analyzers)
            if (analyzer.Thread == thread)
                return true;
        return false;
    }

    /// <summary>Adds an analyzer; refused for an id already registered.</summary>
    public Result<IDisposable, Reason> Register(AnalyzerDescriptor analyzer)
    {
        foreach (var existing in _analyzers)
            if (existing.Id == analyzer.Id)
                return Result<IDisposable, Reason>.Error(
                    Reason.Of(ReasonCodes.AlreadyRegistered, analyzer.Id, ValueMap<string, string>.Of("analyzer", analyzer.Id.Value)));
        var next = new AnalyzerDescriptor[_analyzers.Length + 1];
        Array.Copy(_analyzers, next, _analyzers.Length);
        next[_analyzers.Length] = analyzer;
        _analyzers = next;
        return Result<IDisposable, Reason>.Ok(new Registration(this, analyzer));
    }

    /// <summary>The world ends: every registration ends with it.</summary>
    public void End() => _analyzers = [];

    /// <summary>The facts of every analyzer on the thread, appended in registration order.</summary>
    public void Facts(CallbackThread thread, AnalyzerInput input, List<AnalyzerFact> into)
    {
        foreach (var analyzer in _analyzers)
            if (analyzer.Thread == thread)
                foreach (var fact in analyzer.Analyze(input))
                    into.Add(fact);
    }

    private void Remove(AnalyzerDescriptor analyzer)
    {
        var at = Array.IndexOf(_analyzers, analyzer);
        if (at < 0)
            return;
        var next = new AnalyzerDescriptor[_analyzers.Length - 1];
        Array.Copy(_analyzers, 0, next, 0, at);
        Array.Copy(_analyzers, at + 1, next, at, next.Length - at);
        _analyzers = next;
    }

    private sealed class Registration(Analyzers owner, AnalyzerDescriptor analyzer) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            owner.Remove(analyzer);
        }
    }
}
