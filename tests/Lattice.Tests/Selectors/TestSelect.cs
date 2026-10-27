using System;
using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.BuiltWorld;
using Lattice.Core.Ports;
using Lattice.Core.Selectors;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.TestKit.World;
using Lattice.Tests.Board;
using Lattice.Tests.Matching;

namespace Lattice.Tests.Selectors;

/// <summary>
/// A board with the selectors module installed, a fake world, its mirror and the structure selection, over the hand
/// catalogue plus any extra pieces. World reads go through <see cref="Reads"/>, which counts them.
/// </summary>
internal sealed class TestSelect
{
    private long _nextId = 1000;

    public TestSelect(params NamedPiece[] extraPieces)
    {
        T = new TestBoard(default, extraPieces);
        World = new FakeWorld(T.Catalogue);
        Reads = new CountingWorld(World);
        var shapes = new Shapes(T.Catalogue, T.Grid);
        Mirror = new WorldMirror(shapes, Reads);
        Structures = new StructureSelection(shapes, T.Grid, Mirror, Reads);
        Selectors = new GhostSelectors(shapes, T.Grid, Mirror, Reads);
        T.Board.InstallSelectors(Selectors);
    }

    /// <summary>The selectors module the board calls, for asking it without the view's cache.</summary>
    public GhostSelectors Selectors { get; }

    public TestBoard T { get; }

    public FakeWorld World { get; }

    public CountingWorld Reads { get; }

    public WorldMirror Mirror { get; }

    public StructureSelection Structures { get; }

    /// <summary>The turn that sends a piece's +z along +x: a straight that runs along x.</summary>
    public Turn AlongX => TestBoard.TurnSending(Direction.ZPlus, Direction.XPlus, T.Grid);

    public void Start() => Mirror.Start(World, Reads);

    public GhostId Build(string piece, Cell at, Turn turn = default) =>
        Place(new Intent.Build(new Placement(T.Catalogue.Named(piece).Key, at, turn), Option<Finish>.None));

    public GhostId Place(Intent intent, ValueSet<GroupName>? groups = null, ValueMap<AttachmentKey, Attachment>? attached = null) =>
        Assert.Single(T.Placed(new GhostDraft(intent, Option<PlanId>.None, TestBoard.TestMaker, new Visibility.Everyone(), Option<Label>.None,
            Option<Tint>.None, groups ?? ValueSet<GroupName>.Empty, attached ?? ValueMap<AttachmentKey, Attachment>.Empty)).Added).Id;

    /// <summary>A structure in the world as loaded (before <see cref="Start"/>).</summary>
    public StructureRef Built(string piece, Cell at, Turn turn = default)
    {
        var info = Info(piece, at, turn);
        World.Load(info);
        return info.Ref;
    }

    public StructureInfo Info(string piece, Cell at, Turn turn = default) =>
        new(new StructureRef(new ReferenceId(++_nextId), T.Catalogue.Named(piece).Key, at), turn, new BuildState.Built(),
            Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);

    public HashSet<GhostId> Pick(Selector selector)
    {
        var picked = new HashSet<GhostId>();
        foreach (var ghost in Ok(T.View.Select(selector)))
            picked.Add(ghost.Id);
        return picked;
    }

    public HashSet<ReferenceId> PickBuilt(StructureSelector selector)
    {
        var picked = new HashSet<ReferenceId>();
        foreach (var structure in Ok(Structures.Select(selector)))
            picked.Add(structure.Id);
        return picked;
    }

    public static HashSet<T> Set<T>(params T[] items) => new(items);

    public static Cell At(int x, int y, int z) => new(x, y, z);

    public static T Ok<T>(Result<T, Reason> result) where T : notnull =>
        result.TryGet(out var value) ? value : throw new InvalidOperationException(result.ToString());

    /// <summary>Pieces read from catalogue file text (format 2).</summary>
    public static NamedPiece[] Pieces(string text)
    {
        var catalogue = TestKitIds.MustRead(CatalogueFile.Read(new StringReader("lattice-catalogue 3\n" + text), "inline.lcat"));
        var pieces = new NamedPiece[catalogue.Pieces.Count];
        for (var i = 0; i < pieces.Length; i++)
            pieces[i] = catalogue.Pieces[i];
        return pieces;
    }
}
