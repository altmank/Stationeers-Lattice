using Lattice.Api;

namespace Lattice.Core.BuiltWorld;

/// <summary>One change the world mirror took in from the world's signals, as the structure was and is.</summary>
internal abstract record MirrorChange
{
    private MirrorChange() { }

    /// <summary>A structure the mirror did not hold: registered, or first named by a signal and read once.</summary>
    public sealed record Appeared(StructureInfo Now) : MirrorChange;

    /// <summary>A held structure changed: build state, paint, name, or its cell and turn (moved).</summary>
    public sealed record Altered(StructureInfo Before, StructureInfo Now) : MirrorChange;

    /// <summary>A held structure left the world.</summary>
    public sealed record Gone(StructureInfo Before) : MirrorChange;
}
