using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Catalogue;
using Lattice.Core.Grid;
using Lattice.Core.Ports;
using Lattice.TestKit.Catalogue;

namespace Lattice.Tests.Catalogue;

/// <summary>
/// <see cref="IPieceCatalogue.PortsAt"/>: a piece's ports at a cell and turn, in the game's <c>OpenEnds</c> order, with
/// the cell each leaves from and the cell a joining piece takes (Assets.Scripts.Objects/Connection.cs:
/// <c>GetFacingGrid</c> is the end point 0.5 m along its forward, which points into the piece; <c>GetLocalGrid</c> is the
/// end point's own cell).
/// </summary>
public sealed class PortsTests
{
    private const string Text = """
        lattice-catalogue 3
        piece ModPipeStraight kit=ItemModPipe option=0 grid=small
          step material=ItemModPipe*1 exit=ItemWrench air=no
          end 0 0 0 z+ gas reach=340 index=0 game=Pipe
          end 0 0 0 z- gas reach=340 index=1 game=Pipe
          cell 0 0 0 pipe

        piece ModPump kit=ItemModPump option=0 grid=small
          step material=ItemModPump*1 exit=ItemWrench air=no
          end 0 0 0 z- gas reach=273 role=output index=1 game=Pipe
          end 0 0 0 z+ gas reach=275 role=input index=2 game=Pipe
          end 0 0 0 x- power,data reach=257 index=3 game=PowerAndData
          cell 0 0 0 device

        piece ModSilo kit=ItemModSilo option=0 grid=large
          step material=ItemModSilo*1 exit=ItemWrench air=no
          end 5 -5 10 z+ gas reach=350 role=input index=0 game=Pipe
          end -5 -5 -10 z- gas reach=350 role=output index=1 game=Pipe
          end 10 5 0 x+ power reach=380 index=2 game=Power
          cell 0 0 0 centre

        piece ModCrate kit=ItemModCrate option=0 grid=small
          step material=ItemModCrate*1 exit=ItemWrench air=no
          cell 0 0 0 other

        piece ModNetworkPort kit=ItemModNetworkPort option=0 grid=small
          step material=ItemModNetworkPort*1 exit=ItemWrench air=no
          end 0 0 0 y+ mod:ModFluxNetwork:512 reach=300 index=0 game=512

        piece ModOldCable kit=ItemModOldCable option=0 grid=small
          step material=ItemModOldCable*1 exit=ItemWrench air=no
          end 0 0 0 z+ power,data reach=400
          end 0 0 0 z- power,data reach=340
          cell 0 0 0 cable
        """;

    private static readonly Cell At = new(100, 100, 100);
    private static readonly CubeGrid Grid = new();

    private static FakeCatalogue Read(string text = Text) =>
        CatalogueFile.Read(new StringReader(text), "ports.lcat").Match(static c => c, static e => throw new InvalidOperationException(e.ToString()));

    private static PieceCatalogue View(FakeCatalogue catalogue) =>
        new(catalogue.Listing, new CatalogueAdditions(catalogue), PiecePorts.Over(catalogue), Lattice.Core.Styles.Facings.Over(catalogue));

    private static readonly FakeCatalogue Catalogue = Read();

    private static ValueList<Port> Ports(string piece, Cell at, Turn turn = default) =>
        Prefabs.Ok(View(Catalogue).PortsAt(new Placement(Catalogue.Named(piece).Key, at, turn)));

    private static ValueList<Port> Ports(string piece) => Ports(piece, At);

    private static Turn Yaw => Lattice.Tests.Board.TestBoard.TurnSending(Direction.ZPlus, Direction.XPlus, Grid);

    private static IEnumerable<Turn> AllTurns()
    {
        for (var i = 0; i < Turn.Count; i++)
            if (Turn.Create(i).TryGet(out var turn))
                yield return turn;
    }

    public static TheoryData<int> TurnIndexes()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Turn.Count; i++)
            data.Add(i);
        return data;
    }

    [Fact]
    public void A_straight_pipe_at_no_turn_leaves_its_own_cell_both_ways_along_its_axis()
    {
        var ports = Ports("ModPipeStraight");

        Assert.Equal(2, ports.Count);
        Assert.All(ports, port => Assert.Equal(At, port.From));
        Assert.Equal(new[] { Face.PlusZ, Face.MinusZ }, ports.Select(p => p.Toward));
        Assert.Equal(new[] { new Cell(100, 100, 105), new Cell(100, 100, 95) }, ports.Select(p => p.Joins));
        Assert.Equal(new[] { 0, 1 }, ports.Select(p => p.Index.Value));
    }

    [Fact]
    public void A_quarter_yaw_turns_the_ports_with_the_pipe()
    {
        var ports = Ports("ModPipeStraight", At, Yaw);

        Assert.Equal(new[] { Face.PlusX, Face.MinusX }, ports.Select(p => p.Toward));
        Assert.Equal(new[] { new Cell(105, 100, 100), new Cell(95, 100, 100) }, ports.Select(p => p.Joins));
        Assert.All(ports, port => Assert.Equal(At, port.From));
    }

    [Theory]
    [MemberData(nameof(TurnIndexes))]
    public void At_every_turn_a_devices_ports_are_the_ends_runs_join(int index)
    {
        var turn = Prefabs.Ok(Turn.Create(index));
        foreach (var piece in new[] { "ModPump", "ModSilo" })
        {
            var placement = new Placement(Catalogue.Named(piece).Key, At, turn);
            var shape = Prefabs.Ok(new Shapes(Catalogue, Grid).Of(placement));

            var ports = Prefabs.Ok(View(Catalogue).PortsAt(placement));

            Assert.Equal(shape.Ends.Count, ports.Count);
            for (var i = 0; i < ports.Count; i++)
            {
                Assert.Equal(shape.Ends[i].Cell, ports[i].From);
                Assert.Equal(shape.Ends[i].Facing.ToFace(), ports[i].Toward);
                Assert.Equal(Grid.Neighbour(shape.Ends[i].Cell, shape.Ends[i].Facing, GridSize.Small), ports[i].Joins);
            }
        }
    }

    [Fact]
    public void Ports_keep_the_games_index_past_an_entry_with_no_network()
    {
        Assert.Equal(new[] { 1, 2, 3 }, Ports("ModPump").Select(p => p.Index.Value));
    }

    [Fact]
    public void A_cable_end_joins_power_and_data_under_the_games_name()
    {
        var power = Ports("ModPump")[2];

        Assert.Equal(ValueSet.From(new[] { PortKinds.Power, PortKinds.Data }), power.Kinds);
        Assert.Equal("PowerAndData", power.GameName);
    }

    [Fact]
    public void A_devices_inputs_and_outputs_carry_their_roles_and_a_plain_run_end_none()
    {
        Assert.Equal(
            new[] { Option.Some(PortRole.Output), Option.Some(PortRole.Input), Option<PortRole>.None },
            Ports("ModPump").Select(p => p.Role));
        Assert.All(Ports("ModPipeStraight"), port => Assert.True(port.Role.IsNone));
    }

    [Fact]
    public void A_modded_network_kind_is_kept_under_its_bits_the_same_over_two_loads()
    {
        var first = Assert.Single(Prefabs.Ok(View(Read()).PortsAt(new Placement(Catalogue.Named("ModNetworkPort").Key, At, default))));
        var second = Assert.Single(Prefabs.Ok(View(Read()).PortsAt(new Placement(Catalogue.Named("ModNetworkPort").Key, At, default))));

        Assert.Equal("bits-512", Assert.Single(first.Kinds).Value);
        Assert.Equal("512", first.GameName);
        Assert.Equal(first, second);
    }

    [Fact]
    public void A_file_without_indexes_numbers_ports_in_order_and_names_them_from_their_kinds()
    {
        var ports = Ports("ModOldCable");

        Assert.Equal(new[] { 0, 1 }, ports.Select(p => p.Index.Value));
        Assert.All(ports, port => Assert.Equal("PowerAndData", port.GameName));
    }

    [Theory]
    [InlineData("gas", "Pipe")]
    [InlineData("power", "Power")]
    [InlineData("gas,chute", "Pipe, Chute")]
    [InlineData("gas,power,data", "Pipe, PowerAndData")]
    [InlineData("liquid", "PipeLiquid")]
    [InlineData("power,mod:ModFluxNetwork:512", "514")]
    public void A_kind_name_made_from_kinds_reads_as_the_games_flags_name(string kinds, string name)
    {
        Assert.Equal(name, PortNames.GameName(CatalogueText.ParseKinds(kinds).Reduce(ValueSet<NetworkType>.Empty)));
    }

    [Fact]
    public void A_large_device_leaves_from_small_cells_offset_from_its_own_cell_and_turned_with_it()
    {
        var at = new Cell(110, 110, 110);

        var straight = Ports("ModSilo", at);
        var yawed = Ports("ModSilo", at, Yaw);

        Assert.Equal(new[] { new Cell(115, 105, 120), new Cell(105, 105, 100), new Cell(120, 115, 110) }, straight.Select(p => p.From));
        Assert.Equal(new[] { new Cell(115, 105, 125), new Cell(105, 105, 95), new Cell(125, 115, 110) }, straight.Select(p => p.Joins));
        Assert.Equal(new[] { new Cell(120, 105, 105), new Cell(100, 105, 115), new Cell(110, 115, 100) }, yawed.Select(p => p.From));
        Assert.Equal(new[] { Face.PlusX, Face.MinusX, Face.MinusZ }, yawed.Select(p => p.Toward));
    }

    [Fact]
    public void A_built_structures_placement_gives_the_ports_of_a_ghost_at_the_same_placement()
    {
        var piece = Catalogue.Named("ModPump").Key;
        var structure = new StructureInfo(new StructureRef(new ReferenceId(42), piece, At), Yaw, new BuildState.Built(),
            Option<PaintColour>.None, Option<ThingName>.None, Option<Setting>.None);
        var ghost = new Intent.Build(new Placement(piece, At, Yaw), Option<Finish>.None);

        Assert.Equal(
            Prefabs.Ok(View(Catalogue).PortsAt(ghost.Target)),
            Prefabs.Ok(View(Catalogue).PortsAt(new Placement(structure.Ref.Piece, structure.Ref.At, structure.Turn))));
    }

    [Fact]
    public void An_unknown_piece_is_refused_and_a_piece_with_no_ports_gives_none()
    {
        var why = Prefabs.Error(View(Catalogue).PortsAt(new Placement(PrefabHash.Piece("NoSuchPiece"), At, default)));

        Assert.Equal(ReasonCodes.UnknownPiece, why.Code);
        Assert.Empty(Ports("ModCrate"));
    }

    [Fact]
    public void Asking_again_allocates_the_list_and_its_ports_alone()
    {
        var view = View(Catalogue);
        var placement = new Placement(Catalogue.Named("ModPump").Key, At, Yaw);
        view.PortsAt(placement);

        var answer = Prefabs.Ok(view.PortsAt(placement));

        var asked = Allocated(() => view.PortsAt(placement));
        var copied = Allocated(() => Result<ValueList<Port>, Reason>.Ok(ValueList.From(new[] { answer[0] with { }, answer[1] with { }, answer[2] with { } })));

        Assert.True(asked <= copied, $"{asked} B to ask; the list and its ports alone take {copied} B");
    }

    private static long Allocated(Func<object> ask)
    {
        ask();
        var before = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(ask());
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [GameCatalogueFact]
    public void The_games_catalogue_numbers_every_end_as_the_game_does_and_names_its_kinds_as_the_game_does()
    {
        var catalogue = GameCatalogue.Load();
        var ends = 0;
        foreach (var named in catalogue.Pieces)
        {
            var last = -1;
            foreach (var end in named.Facts.Ends)
            {
                Assert.True(end.GameIndex.TryGet(out var index), $"{named.Name}: an end without its game index");
                Assert.True(index > last, $"{named.Name}: index {index} after {last}");
                last = index;
                Assert.True(end.GameName.TryGet(out var name), $"{named.Name}: an end without its kind name");
                if (end.Kinds.All(static k => k is not NetworkType.Unknown))
                    Assert.Equal(name, PortNames.GameName(end.Kinds));
                ends++;
            }
        }

        Assert.True(ends > 0);
    }

    /// <summary>
    /// The game's own cells for an end of a large device, worked from a turned point in single precision, as
    /// <c>Connection.SetGrids</c> does: <c>LocalGrid</c> is the end point's small cell, <c>FacingGrid</c> the cell 0.5 m
    /// along the end's forward (into the piece); small cells by <c>GridCenter(0.5, 0.25)</c>, whose centres lie every half
    /// metre (Assets.Scripts.Util/ExtensionMethods.cs), then <c>Grid3</c>'s round half away from zero
    /// (Assets.Scripts.GridSystem/Grid3.cs).
    /// </summary>
    [GameCatalogueFact]
    public void Large_devices_from_the_game_leave_from_and_join_the_cells_the_game_works_out_at_every_turn()
    {
        var catalogue = GameCatalogue.Load();
        var view = View(catalogue);
        var at = new Cell(110, 30, -90);
        var checkedEnds = 0;
        foreach (var named in catalogue.Pieces)
        {
            var piece = named.Facts;
            if (piece.Grid != GridSize.Large || piece.Ends.Count == 0)
                continue;
            foreach (var turn in AllTurns())
            {
                var ports = Prefabs.Ok(view.PortsAt(new Placement(piece.Key, at, turn)));
                for (var i = 0; i < ports.Count; i++)
                {
                    var end = piece.Ends[i];
                    var (joins, from) = GameCells(at, turn, end);
                    Assert.True(joins == ports[i].Joins && from == ports[i].From,
                        $"{named.Name} end {i} turn {turn.Index}: game {from} -> {joins}, ports {ports[i].From} -> {ports[i].Joins}");
                    checkedEnds++;
                }
            }
        }

        Assert.True(checkedEnds > 0, "no large device with ports in the game's catalogue");
    }

    private static (Cell Joins, Cell From) GameCells(Cell at, Turn turn, ConnectionEnd end)
    {
        var outward = end.Facing.Unit();
        var reach = end.Reach.Reduce(new Millimetres(375)).Value / 1000f;
        var local = new Vector3(end.At.X / 10f + outward.X * reach, end.At.Y / 10f + outward.Y * reach, end.At.Z / 10f + outward.Z * reach);
        var rotation = Rotation(turn.Axes);
        var point = new Vector3(at.X / 10f, at.Y / 10f, at.Z / 10f) + Vector3.Transform(local, rotation);
        var forward = Vector3.Transform(new Vector3(-outward.X, -outward.Y, -outward.Z), rotation);
        return (SmallCell(point), SmallCell(point + forward * 0.5f));
    }

    private static Quaternion Rotation(TurnAxes axes)
    {
        var (x, y, z) = (Unit(axes.X), Unit(axes.Y), Unit(axes.Z));
        var matrix = new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
        return Quaternion.CreateFromRotationMatrix(matrix);
    }

    private static Vector3 Unit(SignedAxis axis)
    {
        var sign = axis.Sign == AxisSign.Minus ? -1f : 1f;
        return axis.Axis switch
        {
            Axis.X => new Vector3(sign, 0, 0),
            Axis.Y => new Vector3(0, sign, 0),
            _ => new Vector3(0, 0, sign),
        };
    }

    private static Cell SmallCell(Vector3 point) => new(Key(point.X), Key(point.Y), Key(point.Z));

    private static int Key(float metres)
    {
        var centre = MathF.Round((metres - 0.5f) / 0.5f) * 0.5f + 0.5f;
        var decimetres = centre * 10f;
        return decimetres >= 0f ? (int)(decimetres + 0.5f) : (int)(decimetres - 0.5f);
    }
}
