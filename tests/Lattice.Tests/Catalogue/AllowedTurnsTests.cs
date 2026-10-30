using System.Collections.Generic;
using System.IO;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Ports;
using Lattice.Core.Transforms;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using Lattice.Tests.EntryPoint;
using Lattice.Tests.Runs;
using Lattice.Tests.Selectors;
using Lattice.Tests.Transforms;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// Each piece's allowed turns (<see cref="PieceEntry.AllowedTurns"/>): the turns the game's builder reaches from its
/// <c>RotationAxis</c> and <c>AllowedRotations</c> (decompile <c>Assets.Scripts.Inventory/InventoryManager.cs</c>
/// <c>UpdatePlacement</c>, <c>PlacementMode</c>); what the catalogue reads and writes; and Lattice keeping to them: a
/// ghost in another turn fails <c>turn-not-allowed</c>, transforms take an allowed twin or refuse, and runs lay allowed
/// turns or say why.
/// </summary>
public sealed class AllowedTurnsTests
{
    /// <summary>Pieces the builder turns about up only: a device with a direction, a frame with none, and a rail.</summary>
    internal const string YawOnlyText = """
        lattice-catalogue 3
        piece ModYawDevice kit=ItemModYawDevice option=0 grid=small turning=grid/y/wcf
          step material=ItemModYawDevice*1 exit=ItemWrench air=no
          end 0 0 0 z+ gas role=input
          cell 0 0 0 device
          collision 4

        piece ModYawBlock kit=ItemModYawBlock option=0 grid=large turning=grid/y/wcf
          step material=ItemModYawBlock*1 exit=ItemWrench air=yes gravity=yes
          cell 0 0 0 centre

        piece ModYawRail kit=ItemModYawRail option=0 grid=small turning=grid/y/wcf
          step material=ItemModYawRail*1 exit=ItemWireCutters air=no
          end 0 0 0 z+ power
          end 0 0 0 z- power
          cell 0 0 0 cable
          collision 2
        """;

    private static readonly Turn Yaw = Turn.Of(SignedAxis.ZMinus, SignedAxis.YPlus).TryGet(out var t) ? t : default;

    /// <summary>A quarter tilt about +X: +Y to +Z.</summary>
    private static readonly Turn Tilt = Turn.Of(SignedAxis.XPlus, SignedAxis.ZPlus).TryGet(out var t) ? t : default;

    /// <summary>A half turn about +X.</summary>
    private static readonly Turn Flip = Turn.Of(SignedAxis.XPlus, SignedAxis.YMinus).TryGet(out var t) ? t : default;

    private static TurnSet Of(PlacedBy placed, RotationAxes axes, StartFaces faces = StartFaces.All, bool mounted = false) =>
        new TurnLimits(placed, axes, faces, mounted).Turns;

    private static List<SignedAxis> Forwards(TurnSet set)
    {
        var forwards = new List<SignedAxis>();
        foreach (var turn in set.ToList())
            forwards.Add(turn.Axes.Z);
        return forwards;
    }

    [Fact]
    public void A_set_holds_counts_lists_and_prints_its_turns()
    {
        var set = TurnSet.From([Turn.None, Tilt]);

        Assert.True(set.Contains(Tilt));
        Assert.False(set.Contains(Yaw));
        Assert.Equal(2, set.Count);
        Assert.Equal(new[] { Turn.None, Tilt }, set.ToList());
        Assert.Equal($"0,{Tilt.Index}", set.ToString());
        Assert.Equal("all", TurnSet.All.ToString());
        Assert.Equal("none", default(TurnSet).ToString());
        Assert.Equal(24, TurnSet.All.Count);
        Assert.True(TurnSet.All.IsAll);
        Assert.Equal(TurnSet.All, TurnSet.From(TurnSet.All.ToList()));
    }

    [Fact]
    public void A_piece_with_the_games_defaults_has_every_turn_however_it_is_placed()
    {
        Assert.Equal(TurnSet.All, TurnLimits.Free.Turns);
        Assert.Equal(TurnSet.All, Of(PlacedBy.Face, RotationAxes.All));
        Assert.Equal(TurnSet.All, Of(PlacedBy.FaceMount, RotationAxes.All));
    }

    [Fact]
    public void In_the_grid_one_axis_gives_four_turns_two_give_all_and_none_gives_the_start_alone()
    {
        Assert.Equal(TurnSet.From([Turn.None, Yaw, Compose(Yaw, Yaw), Compose(Compose(Yaw, Yaw), Yaw)]), Of(PlacedBy.Grid, RotationAxes.Y));
        Assert.Equal(4, Of(PlacedBy.Grid, RotationAxes.X).Count);
        Assert.Equal(4, Of(PlacedBy.Grid, RotationAxes.Z).Count);
        Assert.Equal(TurnSet.All, Of(PlacedBy.Grid, RotationAxes.X | RotationAxes.Y));
        Assert.Equal(TurnSet.All, Of(PlacedBy.Grid, RotationAxes.Y | RotationAxes.Z));
        Assert.Equal(TurnSet.From([Turn.None]), Of(PlacedBy.Grid, RotationAxes.None));
    }

    [Fact]
    public void A_mounted_piece_tilts_a_half_turn_so_x_and_y_give_eight_turns()
    {
        Assert.Equal(TurnSet.From([Turn.None, Flip]), Of(PlacedBy.Grid, RotationAxes.X, mounted: true));
        var eight = Of(PlacedBy.Grid, RotationAxes.X | RotationAxes.Y, mounted: true);
        Assert.Equal(8, eight.Count);
        foreach (var turn in eight.ToList())
            Assert.Equal(Axis.Y, turn.Axes.Y.Axis);
    }

    [Fact]
    public void A_piece_that_may_not_start_on_a_wall_starts_tilted_onto_the_ceiling_or_the_floor()
    {
        Assert.All(Forwards(Of(PlacedBy.Grid, RotationAxes.Y, StartFaces.Ceiling)), forward => Assert.Equal(SignedAxis.YMinus, forward));
        Assert.All(Forwards(Of(PlacedBy.Grid, RotationAxes.Y, StartFaces.Floor)), forward => Assert.Equal(SignedAxis.YPlus, forward));
        Assert.Equal(4, Of(PlacedBy.Grid, RotationAxes.Y, StartFaces.Floor).Count);
        Assert.Equal(TurnSet.From([Turn.None]), Of(PlacedBy.Grid, RotationAxes.None, StartFaces.Wall));
    }

    [Fact]
    public void On_a_face_up_spins_on_the_sides_x_tilts_to_floor_and_ceiling_and_back_and_z_rolls()
    {
        Assert.Equal(Of(PlacedBy.Grid, RotationAxes.Y), Of(PlacedBy.Face, RotationAxes.Y));
        var tilts = Of(PlacedBy.Face, RotationAxes.X);
        Assert.Equal(3, tilts.Count);
        var forwards = Forwards(tilts);
        Assert.Contains(SignedAxis.ZPlus, forwards);
        Assert.Contains(SignedAxis.YMinus, forwards);
        Assert.Contains(SignedAxis.YPlus, forwards);
        var rolls = Of(PlacedBy.Face, RotationAxes.Z);
        Assert.Equal(4, rolls.Count);
        Assert.All(Forwards(rolls), forward => Assert.Equal(SignedAxis.ZPlus, forward));
        Assert.Equal(TurnSet.All, Of(PlacedBy.Face, RotationAxes.X | RotationAxes.Y | RotationAxes.Z, StartFaces.Wall));
        Assert.Equal(TurnSet.All, Of(PlacedBy.FaceMount, RotationAxes.None, StartFaces.Wall));
    }

    [Fact]
    public void Smart_rotate_steps_a_one_axis_piece_through_its_lists_rotations_bent_onto_that_axis()
    {
        var roll = Turn.Of(SignedAxis.YPlus, SignedAxis.XMinus).TryGet(out var r) ? r : default;
        var flat = new TurnLimits(PlacedBy.Grid, RotationAxes.Y, StartFaces.All, false) { Smart = TurnSet.From([roll]) };
        var exhaustive = flat with { Smart = TurnSet.From([Yaw, roll]) };

        Assert.Equal(Of(PlacedBy.Grid, RotationAxes.Y), flat.Turns);
        Assert.Equal(TurnSet.All, exhaustive.Turns);
        Assert.Equal(TurnSet.From([Turn.None]), (exhaustive with { Axes = RotationAxes.None }).Turns);
        Assert.Equal(4, (new TurnLimits(PlacedBy.Grid, RotationAxes.X, StartFaces.All, false) { Smart = TurnSet.From([roll]) }).Turns.Count);
    }

    [Fact]
    public void The_turning_word_carries_the_smart_rotate_turns()
    {
        var limits = new TurnLimits(PlacedBy.Grid, RotationAxes.Y, StartFaces.All, true) { Smart = TurnSet.From([Turn.None, Yaw, Tilt]) };

        var text = CatalogueText.Turning(limits);

        Assert.Equal($"grid/y/wcf/mounted/smart-0.1.{Tilt.Index}", text);
        Assert.Equal(limits, CatalogueText.ParseTurning(text).Reduce(TurnLimits.Free));
        Assert.Equal(limits with { Mounted = false }, CatalogueText.ParseTurning("grid/y/wcf/smart-0.1." + Tilt.Index).Reduce(TurnLimits.Free));
    }

    [Fact]
    public void Pieces_that_share_a_builder_cursor_share_its_turns_so_one_smart_rotated_piece_tilts_the_others()
    {
        var roll = Turn.Of(SignedAxis.YPlus, SignedAxis.XMinus).TryGet(out var r) ? r : default;
        var plain = new TurnLimits(PlacedBy.Grid, RotationAxes.Y, StartFaces.All, false);
        var tilting = plain with { Smart = TurnSet.From([Yaw, roll]) };
        var wall = new TurnLimits(PlacedBy.Grid, RotationAxes.Y, StartFaces.Wall, false);

        var shared = BuilderTurns.Shared([plain, tilting, wall]);

        Assert.Equal(4, plain.Turns.Count);
        Assert.Equal(TurnSet.All, shared(plain));
        Assert.Equal(TurnSet.All, shared(tilting));
        Assert.Equal(4, shared(wall).Count);
        Assert.Equal(TurnSet.All, shared(TurnLimits.Free));
        Assert.Equal(Of(PlacedBy.Grid, RotationAxes.X), shared(new TurnLimits(PlacedBy.Grid, RotationAxes.X, StartFaces.All, false)));
    }

    [GameCatalogueFact]
    public void The_games_doors_turn_about_up_floor_hatches_lie_on_the_floor_and_devices_turn_every_way()
    {
        var catalogue = GameCatalogue.Load();
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));
        TurnSet TurnsOf(string piece) => Prefabs.Ok(view.Piece(PrefabHash.Piece(piece))).AllowedTurns;
        var yaws = Of(PlacedBy.Grid, RotationAxes.Y);

        Assert.Equal(yaws, TurnsOf("StructureAirlock"));
        Assert.Equal(yaws, TurnsOf("StructureCompositeDoor"));
        Assert.Equal(yaws, TurnsOf("StructureRailing"));
        Assert.Equal(yaws, TurnsOf("StructureDrinkingFountain"));
        Assert.Equal(4, TurnsOf("StructureManualFloorHatch").Count);
        Assert.All(Forwards(TurnsOf("StructureManualFloorHatch")), forward => Assert.Equal(SignedAxis.YPlus, forward));
        Assert.Equal(TurnSet.From([Turn.None]), TurnsOf("StructureFrame"));
        Assert.Equal(TurnSet.All, TurnsOf("StructureMediumConvectionRadiator"));
        Assert.Equal(TurnSet.All, TurnsOf("StructureCableStraight"));
        Assert.Equal(TurnsOf("StructureAirlock"), catalogue.Named("StructureAirlock").AllowedTurns);
        var limited = 0;
        foreach (var entry in view.Pieces)
            if (!entry.AllowedTurns.IsAll)
                limited++;
        Assert.Equal(36, limited);
    }

    [Fact]
    public void An_entry_built_without_allowed_turns_has_every_turn_and_the_catalogue_view_gives_the_pieces_own()
    {
        var entry = new PieceEntry(new PrefabKey(1), "Piece", "Piece", PieceGrid.Large, ValueList<KitPlace>.Empty);
        var catalogue = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(YawOnlyText), "yaw.lcat"));
        var view = new PieceCatalogue(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));

        Assert.Equal(TurnSet.All, entry.AllowedTurns);
        Assert.Equal(Of(PlacedBy.Grid, RotationAxes.Y), Prefabs.Ok(view.Piece(catalogue.Named("ModYawDevice").Key)).AllowedTurns);
        Assert.Equal(Of(PlacedBy.Grid, RotationAxes.Y), catalogue.Named("ModYawDevice").AllowedTurns);
    }

    [Fact]
    public void The_data_file_writes_what_limits_a_pieces_turns_only_where_it_is_not_the_games_default_and_reads_it_back()
    {
        var plain = Prefabs.Piece("ModPlain");
        var yaw = Prefabs.Piece("ModYaw") with { Turning = new TurnLimits(PlacedBy.Grid, RotationAxes.Y, StartFaces.Wall, false) };
        var mounted = Prefabs.Piece("ModMounted") with { Turning = new TurnLimits(PlacedBy.Grid, RotationAxes.X | RotationAxes.Z, StartFaces.None, true) };
        var catalogue = new PrefabCatalogue(new CountingSource([Prefabs.Kit("ItemKitMod", plain, yaw, mounted)], [plain, yaw, mounted]));
        var text = new StringWriter();

        CatalogueDataFile.Write(catalogue, catalogue.ReadAll(), System.TimeSpan.Zero, text);
        var written = text.ToString();
        var read = TestKitIds.MustRead(CatalogueFile.Read(new StringReader(written), "turning.lcat"));

        Assert.Equal(2, written.Split(" turning=").Length - 1);
        Assert.Contains("piece ModYaw kit=ItemKitMod option=1 grid=small turning=grid/y/w", written);
        Assert.Contains("turning=grid/xz/none/mounted", written);
        Assert.Equal(TurnLimits.Free, read.Named("ModPlain").Turning);
        Assert.Equal(new TurnLimits(PlacedBy.Grid, RotationAxes.Y, StartFaces.Wall, false), read.Named("ModYaw").Turning);
        Assert.Equal(new TurnLimits(PlacedBy.Grid, RotationAxes.X | RotationAxes.Z, StartFaces.None, true), read.Named("ModMounted").Turning);
    }

    [Theory]
    [InlineData("grid/y")]
    [InlineData("pillar/y/w")]
    [InlineData("grid/yy/w")]
    [InlineData("grid/q/w")]
    [InlineData("grid/y/w/bolted")]
    [InlineData("grid/y/w/smart-1.1")]
    [InlineData("grid/y/w/smart-24")]
    [InlineData("grid/y/w/smart-1/mounted")]
    [InlineData("grid//w")]
    public void A_turning_word_that_does_not_read_is_refused(string text) =>
        Assert.False(CatalogueText.ParseTurning(text).IsSome);

    [Fact]
    public void A_ghost_in_a_turn_its_builder_cannot_give_fails_turn_not_allowed_and_one_in_an_allowed_turn_does_not()
    {
        using var bench = new WorldBench(YawOnlyText);
        var world = bench.Start();
        var tilted = Placed(bench, "ModYawDevice", new Cell(105, 100, 100), Tilt);
        var yawed = Placed(bench, "ModYawDevice", new Cell(115, 100, 100), Yaw);
        bench.Settle();

        var findings = Ready(world, tilted, yawed);

        var failed = Assert.IsType<Finding.WillFail>(findings.Find(f => f.Ghost == tilted));
        Assert.Equal(ReasonCodes.TurnNotAllowed, failed.Why.Code);
        Assert.Equal("105,100,100", failed.Why.Facts["cell"]);
        Assert.Equal(Tilt.Index.ToString(), failed.Why.Facts["turn"]);
        Assert.Equal(bench.Runs.Catalogue.Named("ModYawDevice").Key.Hash.ToString(), failed.Why.Facts["piece"]);
        Assert.DoesNotContain(findings, f => f.Ghost == yawed && f is Finding.WillFail { Why.Code: var code } && code == ReasonCodes.TurnNotAllowed);
    }

    [Fact]
    public void A_piece_with_no_direction_passes_in_any_turn_because_the_game_builds_it_in_an_allowed_one_and_it_matches()
    {
        using var bench = new WorldBench(YawOnlyText);
        var world = bench.Start();
        var tilted = Placed(bench, "ModYawBlock", new Cell(110, 110, 110), Tilt);
        bench.Settle();

        Assert.DoesNotContain(Ready(world, tilted), f => f is Finding.WillFail { Why.Code: var code } && code == ReasonCodes.TurnNotAllowed);
    }

    [Fact]
    public void A_turn_that_tilts_a_piece_its_builder_only_yaws_is_refused_and_a_yaw_is_applied()
    {
        var b = new TransformBench(YawOnlyText);
        var device = b.Build("ModYawDevice", new Cell(105, 105, 105));
        var before = b.Placed();

        var refused = Lattice.Tests.Board.TestBoard.Refused(b.Transform(TransformBench.All, new Transformation.Rotate(new Cell(105, 105, 105), Tilt)));
        Assert.Equal(ReasonCodes.TurnNotAllowed, refused.Why.Code);
        Assert.Equal(new[] { device }, refused.Ghosts);
        Assert.Equal(b.T.Catalogue.Named("ModYawDevice").Key.Hash.ToString(), refused.Why.Facts["pieces"]);
        Assert.Equal(before, b.Placed());

        Assert.IsType<Outcome.Applied>(b.Transform(TransformBench.All, new Transformation.Rotate(new Cell(105, 105, 105), Yaw)));
        Assert.Equal(TransformProblem.TurnNotAllowed, b.Placements.Apply(new Transformation.Rotate(new Cell(105, 105, 105), Tilt),
            new Placement(b.T.Catalogue.Named("ModYawDevice").Key, new Cell(105, 105, 105), default)).ErrorOf());
    }

    [Fact]
    public void A_transform_that_lands_a_piece_in_a_turn_not_allowed_takes_the_allowed_turn_that_looks_the_same()
    {
        var b = new TransformBench(YawOnlyText);
        var rail = b.T.Catalogue.Named("ModYawRail").Key;

        var flipped = b.Placements.Apply(new Transformation.Rotate(new Cell(105, 105, 105), Flip), new Placement(rail, new Cell(105, 105, 105), default));

        Assert.True(flipped.TryGet(out var placed));
        Assert.Equal(new Placement(rail, new Cell(105, 105, 105), Turn.None), placed);
    }

    [Fact]
    public void A_run_lays_a_yaw_only_rail_flat_in_allowed_turns_and_reports_turn_not_allowed_where_it_must_stand_up()
    {
        var bench = new RunBench(YawOnlyText);
        var rail = bench.KitOf("ModYawRail");
        var allowed = bench.Catalogue.Named("ModYawRail").AllowedTurns;

        var across = bench.Plan(bench.Run(rail, new Cell(105, 105, 105), new Cell(145, 105, 105)));
        var up = bench.Plan(bench.Run(rail, new Cell(105, 105, 105), new Cell(105, 145, 105)));

        Assert.False(across.FirstFailure.IsSome);
        Assert.NotEmpty(across.Pieces);
        Assert.All(across.Pieces, piece => Assert.True(allowed.Contains(piece.Turn), piece.ToString()));
        Assert.True(up.FirstFailure.TryGet(out var failure));
        Assert.Equal(new Cell(105, 105, 105), failure.At);
        Assert.Equal(ReasonCodes.TurnNotAllowed, failure.Why.Code);
    }

    [Fact]
    public void A_run_picks_an_allowed_turn_where_a_lower_one_lays_the_same_cells()
    {
        var bench = new RunBench("""
            lattice-catalogue 3
            piece ModTiltRail kit=ItemModTiltRail option=0 grid=small turning=grid/x/wcf
              step material=ItemModTiltRail*1 exit=ItemWireCutters air=no
              end 0 0 0 z+ power
              end 0 0 0 z- power
              cell 0 0 0 cable
              collision 2
            """);
        var allowed = bench.Catalogue.Named("ModTiltRail").AllowedTurns;

        var up = bench.Plan(bench.Run(bench.KitOf("ModTiltRail"), new Cell(105, 105, 105), new Cell(105, 145, 105)));

        Assert.False(up.FirstFailure.IsSome);
        Assert.All(up.Pieces, piece => Assert.True(allowed.Contains(piece.Turn), piece.ToString()));
    }

    [Fact]
    public void A_route_lays_a_yaw_only_rail_in_allowed_turns()
    {
        var bench = new Lattice.Tests.Routes.RouteBench(extraPieces: """
            piece ModYawRail kit=ItemModYawRail option=0 grid=small turning=grid/y/wcf
              step material=ItemModYawRail*1 exit=ItemWireCutters air=no
              end 0 0 0 z+ power
              end 0 0 0 z- power
              cell 0 0 0 cable
              collision 2
            """);
        var allowed = bench.Catalogue.Named("ModYawRail").AllowedTurns;

        var plan = bench.Plan(bench.Route(bench.Runs.KitOf("ModYawRail"), new Cell(105, 105, 105), new Cell(145, 105, 105)));

        Assert.False(plan.Run.FirstFailure.IsSome);
        Assert.NotEmpty(plan.Run.Pieces);
        Assert.All(plan.Run.Pieces, piece => Assert.True(allowed.Contains(piece.Turn), piece.ToString()));
    }

    private static Turn Compose(Turn first, Turn then) => new Lattice.Core.Grid.TurnTable().Compose(first, then);

    private static GhostId Placed(WorldBench bench, string piece, Cell at, Turn turn) =>
        Assert.IsType<Outcome.Applied>(bench.Submit(new Command.Place(ValueList.Of(
            Lattice.Tests.Board.TestBoard.Draft(new Intent.Build(new Placement(bench.Runs.Catalogue.Named(piece).Key, at, turn), Option<Finish>.None))))))
            .Changes.Added[0].Id;

    private static List<Finding> Ready(Lattice.Core.EntryPoint.LatticeWorld world, params GhostId[] ghosts)
    {
        var report = world.Validation.Check(new Selector.Ids(ValueList.From(ghosts))).Match(static a => a, static why => throw new Xunit.Sdk.XunitException(why.ToString()));
        return new List<Finding>(Assert.IsType<Answer<ValidationReport>.Ready>(report).Value.Findings);
    }
}
