using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Catalogue;
using Lattice.Core.World;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Transforms;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// Where the game snaps a piece it places on a face (<see cref="PieceSnap.OnFaces"/>): the face cursor puts it at the
/// grid centre it aims into, moved back half a step along its forward (decompile
/// <c>Assets.Scripts.Inventory/InventoryManager.cs</c> <c>UpdatePlacement</c>, <c>PlacementSnap.Face</c>;
/// <c>Assets.Scripts.Util/ExtensionMethods.cs</c> <c>GridCenter</c>), so a wall stands on a grid line on one axis and
/// on centres on the other two. A face-mounted piece stays at a grid centre of its own snap (<c>PlacementSnap.FaceMount</c>).
/// </summary>
public sealed class FaceSnapTests
{
    /// <summary>A wall the game places on a face, and a light it mounts on one.</summary>
    private const string Text = """
        lattice-catalogue 3
        piece ModWall kit=ItemKitModWall option=0 grid=large place=face
          step entry=ItemIronSheets material=ItemIronSheets*2 exit=ItemCrowbar air=yes gravity=yes
          centre 0 0 0.05

        piece ModLight kit=ItemKitModLight option=0 grid=small turning=face-mount/z/wcf
          step material=ItemKitModLight*1 exit=ItemWrench air=no
          cell 0 0 0 device
        """;

    private static readonly PieceSnap Wall = PieceSnap.Of(PieceGrid.Large) with { OnFaces = true };

    /// <summary>A quarter tilt about +X: the piece's forward points down y.</summary>
    private static readonly Turn FacingDown = Turn.Of(SignedAxis.XPlus, SignedAxis.ZPlus).TryGet(out var t) ? t : default;

    private static readonly Vector3[] Forwards =
    [
        new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1),
    ];

    private readonly record struct Vector3(float X, float Y, float Z);

    /// <summary>The game's <c>GridCenter</c>: the nearest point offset plus half a size from a multiple of size, ties to even.</summary>
    private static float GridCentre(float metres, float size, float offset)
    {
        var shift = offset + size * 0.5f;
        return MathF.Round((metres - shift) / size) * size + shift;
    }

    /// <summary>Where the game's face cursor puts a piece of the snap aimed at a point with a forward, keyed as a built one is read.</summary>
    private static Cell FaceCursor(AxisSnap snap, Vector3 aim, Vector3 forward)
    {
        var half = snap.Size / 2f;
        return StructureReading.CellOf(
            GridCentre(aim.X, snap.Size, snap.Offset) - forward.X * half,
            GridCentre(aim.Y, snap.Size, snap.Offset) - forward.Y * half,
            GridCentre(aim.Z, snap.Size, snap.Offset) - forward.Z * half);
    }

    [Fact]
    public void A_wall_against_a_ceiling_and_walls_on_each_face_of_a_cell_hold_and_one_at_the_cells_centre_does_not()
    {
        Assert.True(Wall.Holds(new Cell(10, 40, 10)));
        foreach (var face in new[] { new Cell(0, 10, 10), new Cell(20, 10, 10), new Cell(10, 0, 10), new Cell(10, 20, 10), new Cell(10, 10, 0), new Cell(10, 10, 20) })
            Assert.True(Wall.Holds(face), face.ToString());

        Assert.False(Wall.Holds(new Cell(10, 10, 10)));
        Assert.True(PieceSnap.Of(PieceGrid.Large).Holds(new Cell(10, 10, 10)));
        Assert.False(PieceSnap.Of(PieceGrid.Large).Holds(new Cell(10, 40, 10)));
    }

    [Fact]
    public void A_face_snap_names_the_axis_a_cell_is_off()
    {
        Assert.Equal(Option.Some(Axis.X), Wall.OffAxis(new Cell(5, 40, 10)));
        Assert.Equal(Option.Some(Axis.Y), Wall.OffAxis(new Cell(0, 0, 10)));
        Assert.Equal(Option.Some(Axis.Y), Wall.OffAxis(new Cell(0, 0, 0)));
        Assert.Equal(Option.Some(Axis.X), Wall.OffAxis(new Cell(10, 10, 10)));
        Assert.Equal(Option<Axis>.None, Wall.OffAxis(new Cell(-10, 40, -30)));
        Assert.True((Wall with { Y = default }).Holds(new Cell(10, 13, 10)), "an axis that snaps nothing can be the face's");
        Assert.True(new AxisSnap(2f, 0f).OnLine(40));
        Assert.False(new AxisSnap(2f, 0f).OnLine(30));
    }

    [Fact]
    public void Every_place_the_games_face_cursor_puts_a_wall_holds_its_snap_once_read_back_as_a_built_one()
    {
        var aims = new List<Vector3>();
        for (var x = -3.7f; x < 4f; x += 0.9f)
        for (var y = -1.3f; y < 6f; y += 1.1f)
            aims.Add(new Vector3(x, y, 0.4f * x - 0.3f));

        foreach (var snap in new[] { Wall, Wall with { X = new AxisSnap(2f, 1f), Y = new AxisSnap(2f, 1f), Z = new AxisSnap(2f, 1f) } })
        foreach (var aim in aims)
        foreach (var forward in Forwards)
        {
            var cell = new Cell(FaceCursor(snap.X, aim, forward).X, FaceCursor(snap.Y, aim, forward).Y, FaceCursor(snap.Z, aim, forward).Z);
            Assert.True(snap.Holds(cell), $"{aim} {forward} -> {cell}");
            Assert.False((snap with { OnFaces = false }).Holds(cell), $"{cell} is no cell centre");
        }
    }

    [GameCatalogueFact]
    public void The_games_walls_snap_to_faces_and_its_face_mounted_pieces_to_their_own_grid()
    {
        var catalogue = GameCatalogue.Load();
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));
        PieceEntry EntryOf(string piece) => Prefabs.Ok(view.Piece(PrefabHash.Piece(piece)));

        var wall = EntryOf("StructureWallIron").Snap;
        Assert.True(wall.OnFaces);
        Assert.True(wall.Holds(new Cell(10, 40, 10)));
        Assert.False(wall.Holds(new Cell(10, 30, 10)));
        Assert.True(EntryOf("StructureAirlock").Snap.OnFaces);
        foreach (var entry in view.Pieces)
        {
            Assert.Equal(entry.Placing is PiecePlacing.Face, entry.Snap.OnFaces);
            if (entry.Placing is PiecePlacing.FaceMount)
                Assert.Equal(PieceSnap.Of(entry.Grid), entry.Snap);
        }

        var light = EntryOf("StructureAreaPowerControl").Snap;
        Assert.False(light.OnFaces);
        Assert.True(light.Holds(new Cell(105, 15, 95)));
    }

    [Fact]
    public void A_wall_ghost_on_a_ceiling_passes_the_snap_rule_and_one_at_a_cell_centre_fails_off_snap_naming_its_forward()
    {
        using var bench = new WorldBench(Text);
        var world = bench.Start();
        var wall = bench.Runs.Catalogue.Named("ModWall").Key;
        var onCeiling = Placed(bench, new Placement(wall, new Cell(10, 40, 10), FacingDown));
        var atCentre = Placed(bench, new Placement(wall, new Cell(10, 30, 10), FacingDown));
        var light = Placed(bench, new Placement(bench.Runs.Catalogue.Named("ModLight").Key, new Cell(105, 15, 95), default));
        bench.Settle();

        var findings = Ready(world, onCeiling, atCentre, light);

        Assert.DoesNotContain(findings, f => f.Ghost == onCeiling && OffSnap(f));
        Assert.DoesNotContain(findings, f => f.Ghost == light && OffSnap(f));
        var failed = Assert.IsType<Finding.WillFail>(findings.Find(f => f.Ghost == atCentre && OffSnap(f)));
        Assert.Equal("10,30,10", failed.Why.Facts["cell"]);
        Assert.Equal("y", failed.Why.Facts["axis"]);
    }

    [Fact]
    public void A_wall_moved_by_whole_cells_or_turned_stays_on_a_face()
    {
        var b = new TransformBench(Text);
        var wall = new Placement(b.T.Catalogue.Named("ModWall").Key, new Cell(10, 40, 10), FacingDown);
        var snap = b.T.Catalogue.Named("ModWall").Snap;

        var moved = b.Placements.Apply(new Transformation.Move(new CellOffset(20, -20, 40)), wall);
        var turned = b.Placements.Apply(new Transformation.Rotate(new Cell(10, 10, 10), Turn.Of(SignedAxis.ZMinus, SignedAxis.YPlus).TryGet(out var yaw) ? yaw : default), wall);

        Assert.True(moved.TryGet(out var at), moved.ToString());
        Assert.True(snap.Holds(at.At), at.At.ToString());
        Assert.True(turned.TryGet(out var turnedAt), turned.ToString());
        Assert.True(snap.Holds(turnedAt.At), turnedAt.At.ToString());
    }

    private static bool OffSnap(Finding finding) => finding is Finding.WillFail fail && fail.Why.Code == ReasonCodes.OffSnap;

    private static GhostId Placed(WorldBench bench, Placement placement) =>
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.Place(ValueList.Of(
            Lattice.Tests.Board.TestBoard.Draft(new Intent.Build(placement, Option<Finish>.None))))))
            .Changes.Added[0].Id;

    private static List<Finding> Ready(Lattice.Core.EntryPoint.LatticeWorld world, params GhostId[] ghosts)
    {
        var report = world.Validation.Check(new Selector.Ids(ValueList.From(ghosts))).Match(static a => a, static why => throw new Xunit.Sdk.XunitException(why.ToString()));
        return new List<Finding>(Assert.IsType<Answer<ValidationReport>.Ready>(report).Value.Findings);
    }
}
