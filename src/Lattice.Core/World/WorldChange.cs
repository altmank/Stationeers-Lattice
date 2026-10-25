using Lattice.Api;

namespace Lattice.Core.World;

/// <summary>
/// One thing the game reported about a structure, held by <see cref="WorldChangeRelay"/> until the frame's flush.
/// Union: the cases below are every report the bridge makes.
/// </summary>
internal abstract record WorldChange
{
    private WorldChange() { }

    /// <summary>The structure joined the grid.</summary>
    public sealed record Registered(StructureInfo Info) : WorldChange;

    /// <summary>The structure's build index was set; resolved at the flush, where a removal in the same frame wins.</summary>
    public sealed record StateSet(StructureRef Structure, BuildState State) : WorldChange;

    /// <summary>The structure left the grid for good.</summary>
    public sealed record Deregistered(StructureRef Structure) : WorldChange;

    /// <summary>The structure was painted.</summary>
    public sealed record Painted(StructureRef Structure, PaintColour Colour) : WorldChange;

    /// <summary>The structure was given a name.</summary>
    public sealed record Renamed(StructureRef Structure, ThingName Name) : WorldChange;

    /// <summary>The game took the structure off the grid to move it (a rocket part at launch).</summary>
    public sealed record Detached(StructureRef Structure) : WorldChange;

    /// <summary>The structure's labeller setting was set.</summary>
    public sealed record SettingSet(StructureRef Structure, Setting Setting) : WorldChange;

    /// <summary>The game put a detached structure back on the grid, in a cell and turn.</summary>
    public sealed record Attached(ReferenceId Id, Cell To, Turn Turn) : WorldChange;
}
