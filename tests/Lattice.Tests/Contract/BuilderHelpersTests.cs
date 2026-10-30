using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Lattice.Api;
using Lattice.Core.World;
using UnityEngine;

namespace Lattice.Tests.Contract;

/// <summary>
/// The helpers a builder reads: a cell's world point, a turn's Unity rotation, and the reason codes every builder
/// words the same way. The game's quarter turns are <c>Quaternion.Euler</c> of 90 degrees about each world axis
/// (decompile <c>Assets.Scripts.Util/SmartRotate.cs</c> <c>RotX</c>, <c>RotY</c>, <c>RotZ</c>), and a placed piece takes
/// the cursor's rotation, so the rotations the game places with are the 24 those make.
/// </summary>
public sealed class BuilderHelpersTests
{
    private const string Added = "Added in contract 0.19.";
    private const float Tolerance = 1e-5f;

    private static readonly RuleId From = Ok(RuleId.Create(Ok(ModId.Create("builder")), "orb"));

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0.99f, -0.3f, 2.01f)]
    [InlineData(-123.4f, 55.55f, 1999.9f)]
    [InlineData(-0.26f, 0.24f, -4f)]
    public void A_points_cell_is_at_the_point_the_game_snaps_it_to_on_both_grids(float x, float y, float z)
    {
        var point = new WorldPoint(x, y, z);

        Assert.Equal(new WorldPoint(Centre(x, 2f, 0f), Centre(y, 2f, 0f), Centre(z, 2f, 0f)), Cell.FromWorld(point, PieceGrid.Large).ToWorld());
        Assert.Equal(new WorldPoint(Centre(x, 0.5f, 0.25f), Centre(y, 0.5f, 0.25f), Centre(z, 0.5f, 0.25f)), Cell.FromWorld(point, PieceGrid.Small).ToWorld());
    }

    [Theory]
    [InlineData(0.3f, 0.3f, 0.3f)]
    [InlineData(-3.1f, 7.6f, 12.2f)]
    [InlineData(101.7f, -0.74f, -55.5f)]
    public void An_axis_snapped_pieces_cell_is_at_its_snapped_point_and_reads_back_as_the_same_cell(float x, float y, float z)
    {
        var engine = new PieceSnap(new AxisSnap(2f, 0f), new AxisSnap(0.5f, 0.25f), new AxisSnap(2f, 0f));
        var cell = new Cell(engine.X.Key(x), engine.Y.Key(y), engine.Z.Key(z));

        var point = cell.ToWorld();

        Assert.True(engine.Holds(cell));
        Assert.Equal(new WorldPoint(Centre(x, 2f, 0f), Centre(y, 0.5f, 0.25f), Centre(z, 2f, 0f)), point);
        Assert.Equal(cell, new Cell(engine.X.Key(point.X), engine.Y.Key(point.Y), engine.Z.Key(point.Z)));
    }

    [Fact]
    public void Every_turns_rotation_sends_the_unit_axes_where_its_axes_say()
    {
        foreach (var turn in Turns())
        {
            var rotation = turn.ToRotation();
            var axes = turn.Axes;

            AssertNear(Unit(axes.X), rotation * Vector3.right, turn);
            AssertNear(Unit(axes.Y), rotation * Vector3.up, turn);
            AssertNear(Unit(axes.Z), rotation * Vector3.forward, turn);
            Assert.InRange(rotation.x * rotation.x + rotation.y * rotation.y + rotation.z * rotation.z + rotation.w * rotation.w, 1f - Tolerance, 1f + Tolerance);
        }
    }

    [Fact]
    public void Every_turns_rotation_is_the_one_lattice_places_the_piece_with()
    {
        foreach (var turn in Turns())
        {
            var placed = TurnReading.ToGame(turn);
            Assert.True(Same(turn.ToRotation(), new Quaternion(placed.X, placed.Y, placed.Z, placed.W)), $"turn {turn.Index}");
        }
    }

    [Fact]
    public void The_turns_rotations_are_the_24_the_games_quarter_turns_make()
    {
        var half = (float)Math.Sqrt(0.5);
        Quaternion[] quarters = [new(half, 0f, 0f, half), new(0f, half, 0f, half), new(0f, 0f, half, half)];
        var made = new List<Quaternion> { Quaternion.identity };
        for (var at = 0; at < made.Count; at++)
            foreach (var quarter in quarters)
            {
                var next = quarter * made[at];
                if (!made.Any(q => Same(q, next)))
                    made.Add(next);
            }

        var rotations = Turns().Select(static t => t.ToRotation()).ToList();
        Assert.Equal(Turn.Count, made.Count);
        Assert.All(made, q => Assert.Single(rotations, r => Same(r, q)));
    }

    [Fact]
    public void Needs_items_names_the_item_and_how_many_more()
    {
        var reason = Reason.NeedsItems(From, Ok(ItemQuantity.Create(new ItemKey(-1234), 3)));

        Assert.Equal(ReasonCodes.NeedsItems, reason.Code);
        Assert.Equal("needs-items", reason.Code.Value);
        Assert.Equal(From, reason.From);
        Assert.Equal("-1234", reason.Facts["item"]);
        Assert.Equal("3", reason.Facts["count"]);
        Assert.Equal(2, reason.Facts.Count);
    }

    [Fact]
    public void Out_of_reach_names_the_distance_and_the_reach_in_metres()
    {
        var reason = Reason.OutOfReach(From, Ok(Metres.Create(12.5f)), Ok(Metres.Create(4f)));

        Assert.Equal("out-of-reach", reason.Code.Value);
        Assert.Equal("12.5", reason.Facts["metres"]);
        Assert.Equal("4", reason.Facts["reach"]);
        Assert.Equal(2, reason.Facts.Count);
    }

    [Fact]
    public void No_power_needs_no_facts_and_names_a_device_when_given_one()
    {
        Assert.Equal("no-power", Reason.NoPower(From).Code.Value);
        Assert.Empty(Reason.NoPower(From).Facts);

        var device = Reason.NoPower(From, new ReferenceId(98765));
        Assert.Equal(ReasonCodes.NoPower, device.Code);
        Assert.Equal("98765", device.Facts["device"]);
        Assert.Single(device.Facts);
    }

    [Fact]
    public void Blocked_names_the_cell_and_what_stands_there_when_the_builder_knows()
    {
        var cell = new Cell(-15, 10, 25);

        var unknown = Reason.Blocked(From, cell);
        Assert.Equal("blocked", unknown.Code.Value);
        Assert.Equal("-15,10,25", unknown.Facts["cell"]);
        Assert.False(unknown.Facts.ContainsKey("by"));

        Assert.Single(unknown.Facts);
    }

    [Fact]
    public void Blocked_by_a_ghost_names_the_kind_and_the_ghosts_id()
    {
        var reason = Reason.Blocked(From, new Cell(-15, 10, 25), new GhostId(42));

        Assert.Equal(ReasonCodes.Blocked, reason.Code);
        Assert.Equal(Facts(("cell", "-15,10,25"), ("by", "ghost"), ("ghost", "42")), reason.Facts);
    }

    [Fact]
    public void Blocked_by_a_structure_names_the_kind_and_its_reference_id()
    {
        var reason = Reason.Blocked(From, new Cell(-15, 10, 25), new ReferenceId(7001));

        Assert.Equal(ReasonCodes.Blocked, reason.Code);
        Assert.Equal(Facts(("cell", "-15,10,25"), ("by", "structure"), ("structure", "7001")), reason.Facts);
    }

    [Fact]
    public void Blocked_by_a_player_names_the_kind_and_no_id()
    {
        var reason = Reason.BlockedByPlayer(From, new Cell(-15, 10, 25));

        Assert.Equal(ReasonCodes.Blocked, reason.Code);
        Assert.Equal(Facts(("cell", "-15,10,25"), ("by", "player")), reason.Facts);
    }

    private static ValueMap<string, string> Facts(params (string Key, string Value)[] facts)
    {
        var map = ValueMap<string, string>.Empty;
        foreach (var (key, value) in facts)
            map = map.With(key, value);
        return map;
    }

    [Theory]
    [InlineData("M:Lattice.Api.Cell.ToWorld")]
    [InlineData("M:Lattice.Api.IPieceCatalogue.ItemName(Lattice.Api.ItemKey)")]
    [InlineData("F:Lattice.Api.ReasonCodes.NeedsItems")]
    [InlineData("F:Lattice.Api.ReasonCodes.OutOfReach")]
    [InlineData("F:Lattice.Api.ReasonCodes.NoPower")]
    [InlineData("F:Lattice.Api.ReasonCodes.Blocked")]
    [InlineData("M:Lattice.Api.Reason.NeedsItems(Lattice.Api.RuleId,Lattice.Api.ItemQuantity)")]
    [InlineData("M:Lattice.Api.Reason.OutOfReach(Lattice.Api.RuleId,Lattice.Api.Metres,Lattice.Api.Metres)")]
    [InlineData("M:Lattice.Api.Reason.NoPower(Lattice.Api.RuleId)")]
    [InlineData("M:Lattice.Api.Reason.NoPower(Lattice.Api.RuleId,Lattice.Api.ReferenceId)")]
    [InlineData("M:Lattice.Api.Reason.Blocked(Lattice.Api.RuleId,Lattice.Api.Cell)")]
    [InlineData("M:Lattice.Api.Reason.Blocked(Lattice.Api.RuleId,Lattice.Api.Cell,Lattice.Api.GhostId)")]
    [InlineData("M:Lattice.Api.Reason.Blocked(Lattice.Api.RuleId,Lattice.Api.Cell,Lattice.Api.ReferenceId)")]
    [InlineData("M:Lattice.Api.Reason.BlockedByPlayer(Lattice.Api.RuleId,Lattice.Api.Cell)")]
    public void Each_added_api_member_says_when_it_was_added(string member) =>
        Assert.Contains(Added, Doc(typeof(ApiVersion), member));

    [Theory]
    [InlineData("T:Lattice.Api.TurnRotations")]
    [InlineData("M:Lattice.Api.TurnRotations.ToRotation(Lattice.Api.Turn)")]
    public void Each_added_unity_member_says_when_it_was_added(string member) =>
        Assert.Contains(Added, Doc(typeof(TurnRotations), member));

    // GridCenter on one axis (decompile Assets.Scripts.Util/ExtensionMethods.cs), in double: every centre is a whole
    // number of half metres, so the float it rounds to is exact.
    private static float Centre(float metres, float size, float offset)
    {
        var shift = offset + size / 2.0;
        return (float)(Math.Round((metres - shift) / size, MidpointRounding.ToEven) * size + shift);
    }

    private static IEnumerable<Turn> Turns() => Enumerable.Range(0, Turn.Count).Select(static i => Ok(Turn.Create(i)));

    private static Vector3 Unit(SignedAxis axis)
    {
        var sign = axis.Sign == AxisSign.Minus ? -1f : 1f;
        return axis.Axis switch
        {
            Axis.X => new Vector3(sign, 0f, 0f),
            Axis.Y => new Vector3(0f, sign, 0f),
            _ => new Vector3(0f, 0f, sign),
        };
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, Turn turn) =>
        Assert.True(Math.Abs(expected.x - actual.x) < Tolerance && Math.Abs(expected.y - actual.y) < Tolerance && Math.Abs(expected.z - actual.z) < Tolerance,
            $"turn {turn.Index}: expected {expected.x},{expected.y},{expected.z}, got {actual.x},{actual.y},{actual.z}");

    // q and -q are the same rotation.
    private static bool Same(Quaternion a, Quaternion b)
    {
        var dot = a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w;
        return Math.Abs(Math.Abs(dot) - 1f) < Tolerance;
    }

    private static string Doc(Type inAssembly, string member)
    {
        var docs = XDocument.Load(Path.ChangeExtension(inAssembly.Assembly.Location, ".xml"));
        var entry = docs.Descendants("member").Single(m => (string?)m.Attribute("name") == member);
        return string.Join(" ", entry.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static T Ok<T>(Result<T, Reason> result) where T : notnull => result.TryGet(out var value) ? value : throw new InvalidOperationException(result.ToString());
}
