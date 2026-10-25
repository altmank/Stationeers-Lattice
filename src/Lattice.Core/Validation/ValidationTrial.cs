using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Core.Validation;

/// <summary>
/// Validation of one change not on the board (<see cref="ValidationService.Trial"/>): the findings its added and
/// changed ghosts would get, judged on the worker against the board and the world at the revision it was prepared
/// for, and the mods' worker analyzers' facts for <see cref="Analyze"/>. Pending until the worker has judged it with
/// every main-thread fact it asked for. Main thread.
/// </summary>
internal sealed class ValidationTrial
{
    internal ValidationTrial(ChangeSet changes, ValueList<Ghost> analyze, Revision at)
    {
        Changes = changes;
        Analyze = analyze;
        Answer = new Answer<ValueList<Finding>>.Pending(at);
    }

    /// <summary>The change being judged.</summary>
    public ChangeSet Changes { get; }

    /// <summary>The ghosts the worker analyzers are given.</summary>
    public ValueList<Ghost> Analyze { get; }

    /// <summary>The findings, in build order, once ready; at the board revision before the change.</summary>
    public Answer<ValueList<Finding>> Answer { get; private set; }

    /// <summary>The worker analyzers' facts, in registration order, once ready.</summary>
    public ValueList<AnalyzerFact> Facts { get; private set; } = ValueList<AnalyzerFact>.Empty;

    /// <summary>Whether the findings and the worker analyzers' facts are both in.</summary>
    public bool IsReady => Answer is Answer<ValueList<Finding>>.Ready && !Analyzing;

    /// <summary>Judged: the findings are in (the analyzers may still be running).</summary>
    internal bool Judged => Answer is Answer<ValueList<Finding>>.Ready;

    /// <summary>The worker analyzers are running on their own thread.</summary>
    internal bool Analyzing { get; private set; }

    /// <summary>On the worker now.</summary>
    internal bool Posted { get; set; }

    /// <summary>Its last try asked the main thread for facts; it is tried again once they are all answered.</summary>
    internal bool Asked { get; set; }

    /// <summary>The change's added ghosts and changed ghosts as they would be.</summary>
    public static ValueList<Ghost> GhostsOf(ChangeSet changes)
    {
        var ghosts = new List<Ghost>(changes.Added.Count + changes.Changed.Count);
        foreach (var ghost in changes.Added)
            ghosts.Add(ghost);
        foreach (var change in changes.Changed)
            ghosts.Add(change.After);
        return ValueList.From(ghosts);
    }

    internal void Ready(ValueList<Finding> findings, ValueList<AnalyzerFact> facts)
    {
        Judge(findings, analyzing: false);
        Analyzed(facts);
    }

    internal void Judge(ValueList<Finding> findings, bool analyzing)
    {
        Answer = new Answer<ValueList<Finding>>.Ready(findings, Answer.At);
        Analyzing = analyzing;
    }

    internal void Analyzed(ValueList<AnalyzerFact> facts)
    {
        Facts = facts;
        Analyzing = false;
    }
}
