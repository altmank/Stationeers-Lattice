using Lattice.Api;
using Lattice.Core.Written;

namespace Lattice.Core.Ports;

/// <summary>A game save's point in one plan file: the line the save remembers for the plan.</summary>
/// <param name="Save">The save.</param>
/// <param name="Line">The line.</param>
internal sealed record SaveAt(SaveName Save, LineNumber Line);

/// <summary>
/// The world's plan files as the inspector reads them: every plan this world has a
/// file for (active, hidden by an older save, finished or archived), each file as read, the line each plan is loaded
/// at, and the saves that point into each file. Main thread; <see cref="Read"/> reads the file, linear in its lines.
/// </summary>
internal interface IPlanFiles
{
    /// <summary>Every plan this world has, with or without lines yet, in ascending id.</summary>
    ValueList<PlanId> Plans { get; }

    /// <summary>The plan's file as read; refused <c>not-found</c> for a plan this world never had.</summary>
    Result<PlanFileRead, Reason> Read(PlanId plan);

    /// <summary>The line the plan is loaded at; none when it is not open (hidden, finished) or has no lines.</summary>
    Option<LineNumber> Head(PlanId plan);

    /// <summary>The saves that remember a line of the plan's file.</summary>
    ValueList<SaveAt> Saves(PlanId plan);
}
