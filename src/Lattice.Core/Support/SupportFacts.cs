using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Support;

/// <summary>
/// The support facts of pieces, by piece. None when the catalogue does not carry them for the piece: the rules that
/// need them then leave the piece to the game's own check.
/// </summary>
internal interface ISupportFacts
{
    Option<SupportFacts> Of(PrefabKey piece);
}

/// <summary>The support facts the catalogue read from each piece's prefab (<see cref="PieceFacts.Support"/>).</summary>
internal sealed class CatalogueSupportFacts(ICatalogue catalogue) : ISupportFacts
{
    public Option<SupportFacts> Of(PrefabKey piece) =>
        catalogue.Piece(piece).Match(static facts => facts.Support, static _ => Option<SupportFacts>.None);
}

/// <summary>Support facts for no piece: every rule that needs them asks the game.</summary>
internal sealed class NoSupportFacts : ISupportFacts
{
    public Option<SupportFacts> Of(PrefabKey piece) => Option<SupportFacts>.None;
}
