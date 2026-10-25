using Lattice.Api;
using Lattice.Core.Ports;

namespace Lattice.Core.Support;

/// <summary>One placement to judge: the piece's facts and the world it would be built in.</summary>
/// <param name="Ghost">The ghost asking for it.</param>
/// <param name="At">The placement.</param>
/// <param name="Piece">The piece's catalogue facts.</param>
/// <param name="World">The world with the ghosts before it assumed built.</param>
internal sealed record SupportCase(GhostId Ghost, Placement At, PieceFacts Piece, SupportWorld World);

/// <summary>What one of Lattice's own support rules says about a placement.</summary>
internal abstract record Judgement
{
    private Judgement() { }

    /// <summary>Held up by these structures (real or assumed); empty when it needs none here.</summary>
    /// <param name="By">The supporting structures.</param>
    public sealed record Supported(ValueList<StructureRef> By) : Judgement;

    /// <summary>Nothing holds it up.</summary>
    /// <param name="Why">The reason, with the game's own string for it.</param>
    public sealed record Unsupported(Reason Why) : Judgement;

    /// <summary>Lattice cannot tell from its facts: the game's own check answers.</summary>
    public sealed record AskGame : Judgement;

    /// <summary>The answer turns on ghosts assumed built that the game cannot see: the game answers once they stand.</summary>
    /// <param name="Until">The ghosts.</param>
    public sealed record Uncertain(ValueList<GhostId> Until) : Judgement;

    public static Judgement Nothing => new Supported(ValueList<StructureRef>.Empty);

    public static Judgement Game => new AskGame();
}

/// <summary>One of Lattice's support rules for a census group. Lattice code: a throw is a Lattice bug, never guarded.</summary>
internal interface ICensusRule
{
    RuleId Group { get; }

    Judgement Judge(SupportCase @case);
}
