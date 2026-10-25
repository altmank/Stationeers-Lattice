using System;

namespace Lattice.Api;

/// <summary>One line of a plan file as the inspector shows it.</summary>
/// <param name="Line">Its number.</param>
/// <param name="Follows">The line it follows; none for the first.</param>
/// <param name="Command">The command that wrote it.</param>
/// <param name="By">The actor.</param>
/// <param name="At">When, real time.</param>
/// <param name="Written">The line as written in the file.</param>
public sealed record HistoryEntry(LineNumber Line, Option<LineNumber> Follows, CommandId Command, Actor By, DateTimeOffset At, string Written);

/// <summary>A branch of a plan file: lines that follow an earlier line after an older save was loaded.</summary>
/// <param name="Follows">The line the branch starts after; line 0 for the file's first branch.</param>
/// <param name="Head">Its newest line.</param>
/// <param name="Saves">The game saves whose line lies on it (a save at the line a later branch starts after counts for the earlier one).</param>
public sealed record Branch(LineNumber Follows, LineNumber Head, ValueList<SaveName> Saves);

/// <summary>A ghost's full story, from every line that touched it, across branches.</summary>
/// <param name="Ghost">The ghost.</param>
/// <param name="Entries">The lines, one per change set: in file order, by time when they come from several plans' files.</param>
public sealed record GhostStory(GhostId Ghost, ValueList<HistoryEntry> Entries);

/// <summary>A plan's full story and its branches.</summary>
/// <param name="Plan">The plan.</param>
/// <param name="Entries">The lines from the first to the line the plan is loaded at (to its newest line when it is not open), in order.</param>
/// <param name="Branches">Every branch of its file.</param>
public sealed record PlanStory(PlanId Plan, ValueList<HistoryEntry> Entries, ValueList<Branch> Branches);

/// <summary>
/// A ghost's or plan's story from its plan file, and change-log export. Main thread; reads plan files, so cost is
/// linear in the file's lines. Lattice implements this; consumers call it. A minor may add members, so consumer fakes
/// of it are not covered by compatibility.
/// </summary>
public interface IInspector
{
    /// <summary>A ghost's story, including ghosts no longer on the board; refused for an unknown ghost.</summary>
    Result<GhostStory, Reason> Story(GhostId ghost);

    /// <summary>A plan's story with its branches; refused for an unknown plan.</summary>
    Result<PlanStory, Reason> Story(PlanId plan);

    /// <summary>A plan's change log as written lines, for bug reports; refused for an unknown plan.</summary>
    Result<ValueList<string>, Reason> Export(PlanId plan);
}
