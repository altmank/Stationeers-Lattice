using Lattice.Api;

namespace Lattice.Core.Styles;

/// <summary>
/// The game's paint colours as it shows them, for <see cref="Paint.FinishColour"/>: a finish asks for a paint by the
/// game's colour index, and styles turn that index into a colour here. Main thread; constant cost per call.
/// </summary>
internal interface IPaintColours
{
    /// <summary>The colour the game shows for a paint index; none when the index or the game's colours are unknown.</summary>
    Option<Rgba> Colour(PaintColour paint);
}

/// <summary>No game colours known (offline, tests, a seat whose bridge does not read them): finish paint falls back to its token.</summary>
internal sealed class NoPaintColours : IPaintColours
{
    public Option<Rgba> Colour(PaintColour paint) => Option<Rgba>.None;
}
