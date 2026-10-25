using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Testing;

/// <summary>
/// What a test world starts from: the catalogue, modded pieces added to it, structures standing at the start (or a real
/// save's), the local viewer, and whether heavy checks run inline or on Lattice's own threads. Immutable: every
/// <c>With...</c> returns a new scenario, so one scenario starts any number of equal worlds.
/// <code>
/// var scenario = TestScenario.Empty
///     .WithPiece(conduit)
///     .WithStructure(frame, new Cell(10, 10, 10), default)
///     .As(new Actor.Player(new ClientId(7)));
/// using var world = TestWorld.Start(scenario).Match(w => w, why => throw new Exception(why.ToString()));
/// </code>
/// </summary>
public sealed record TestScenario
{
    private TestScenario()
    {
    }

    /// <summary>The player a scenario's viewer is unless <see cref="As"/> says otherwise.</summary>
    public static Actor DefaultViewer { get; } = new Actor.Player(new ClientId(1));

    /// <summary>The hand catalogue, no structures, viewer player 1, heavy checks inline.</summary>
    public static TestScenario Empty { get; } = new();

    /// <summary>The catalogue the world's pieces come from (the scenario's own pieces are added to it).</summary>
    public TestCatalogue Catalogue { get; private init; } = TestCatalogue.Hand;

    /// <summary>The modded pieces added to the catalogue, in the order given.</summary>
    public ValueList<TestPiece> Pieces { get; private init; } = ValueList<TestPiece>.Empty;

    /// <summary>The save the world's structures are read from first; none for an empty world.</summary>
    public Option<string> Save { get; private init; }

    /// <summary>The local viewer: <see cref="IGhostSystem.Board"/>'s actor and the player the world's hands act for.</summary>
    public Actor Viewer { get; private init; } = DefaultViewer;

    /// <summary>Whether heavy checks and rules run on Lattice's own threads (as in game) rather than inline in each frame.</summary>
    public bool WorkerThreads { get; private init; }

    internal ValueList<Standing> Structures { get; private init; } = ValueList<Standing>.Empty;

    /// <summary>This scenario over another catalogue.</summary>
    public TestScenario WithCatalogue(TestCatalogue catalogue) => this with { Catalogue = catalogue };

    /// <summary>This scenario with one more modded piece in its catalogue.</summary>
    public TestScenario WithPiece(TestPiece piece) => this with { Pieces = Words.Plus(Pieces, piece) };

    /// <summary>This scenario with a fully built structure standing at the start.</summary>
    public TestScenario WithStructure(PrefabKey piece, Cell at, Turn turn) => WithStructure(piece, at, turn, new BuildState.Built());

    /// <summary>This scenario with a structure standing at the start in a given build state.</summary>
    public TestScenario WithStructure(PrefabKey piece, Cell at, Turn turn, BuildState state) =>
        this with { Structures = Words.Plus(Structures, new Standing(piece, at, turn, state)) };

    /// <summary>
    /// This scenario starting from a real save's structures (a <c>.save</c> or a bare <c>world.xml</c> on the
    /// consumer's machine), read with the catalogue; the scenario's own structures stand beside them.
    /// </summary>
    public TestScenario WithSave(string path) => this with { Save = Option.Some(path) };

    /// <summary>This scenario seen by another local viewer.</summary>
    public TestScenario As(Actor viewer) => this with { Viewer = viewer };

    /// <summary>This scenario with heavy checks and rules on Lattice's own threads, as in game; answers then take real time.</summary>
    public TestScenario WithWorkerThreads() => this with { WorkerThreads = true };

    /// <summary>A structure standing at the start.</summary>
    internal sealed record Standing(PrefabKey Piece, Cell At, Turn Turn, BuildState State);
}
