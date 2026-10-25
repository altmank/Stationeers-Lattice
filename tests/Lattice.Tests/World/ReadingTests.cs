using System;
using Lattice.Api;
using Lattice.Core.World;

namespace Lattice.Tests.World;

public sealed class TurnReadingTests
{
    public static TheoryData<int> AllTurns()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Turn.Count; i++)
            data.Add(i);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllTurns))]
    public void Every_turn_reads_back_from_its_game_rotation(int index)
    {
        var turn = Turns.Of(index);
        Assert.Equal(Option.Some(turn), TurnReading.FromGame(TurnReading.ToGame(turn)));
    }

    [Theory]
    [MemberData(nameof(AllTurns))]
    public void Every_turn_has_the_save_loaders_number(int index)
    {
        var q = TurnReading.ToGame(Turns.Of(index));
        Assert.Equal(Option.Some(Turns.Of(index)), Lattice.TestKit.Grid.TurnTable.FromQuaternion(q.X, q.Y, q.Z, q.W));
    }

    [Fact]
    public void No_rotation_is_no_turn() =>
        Assert.Equal(Option.Some(Turn.None), TurnReading.FromGame(new GameRotation(0, 0, 0, 1)));

    [Fact]
    public void A_quarter_yaw_as_unity_writes_it_is_turn_one()
    {
        var half = (float)Math.Sqrt(0.5);
        Assert.Equal(Option.Some(Turns.Of(1)), TurnReading.FromGame(new GameRotation(0, half, 0, half)));
    }

    [Fact]
    public void A_rotation_between_quarter_turns_is_none()
    {
        var s = (float)Math.Sin(Math.PI / 8);
        var c = (float)Math.Cos(Math.PI / 8);
        Assert.True(TurnReading.FromGame(new GameRotation(0, s, 0, c)).IsNone);
    }
}

public sealed class StructureReadingTests
{
    private static GameStructure Frame(int buildIndex = 2, int states = 3, int colour = -1, string name = "") =>
        new(42, 1234, 1.0f, 3.5f, -0.25f, new GameRotation(0, 0, 0, 1), buildIndex, states, colour, name, Option<double>.None);

    [Fact]
    public void Reads_id_piece_and_the_grid3_cell()
    {
        var info = Read(Frame());
        Assert.Equal(new StructureRef(new ReferenceId(42), new PrefabKey(1234), new Cell(10, 35, -3)), info.Ref);
    }

    [Fact]
    public void A_piece_no_catalogue_knows_is_read_like_any_other()
    {
        var modded = Frame() with { PrefabHash = unchecked((int)0xDEADBEEF) };
        Assert.Equal(new PrefabKey(unchecked((int)0xDEADBEEF)), Read(modded).Ref.Piece);
    }

    [Fact]
    public void The_last_build_state_is_built() => Assert.Equal(new BuildState.Built(), Read(Frame(2, 3)).State);

    [Fact]
    public void An_earlier_build_state_is_building_at_that_step() =>
        Assert.Equal(Relays.Building(1), Read(Frame(1, 3)).State);

    [Fact]
    public void A_build_index_below_zero_is_broken() => Assert.Equal(new BuildState.Broken(), Read(Frame(-1, 3)).State);

    [Fact]
    public void A_piece_with_one_build_state_is_built_at_it() => Assert.Equal(new BuildState.Built(), Read(Frame(0, 1)).State);

    [Fact]
    public void Unpainted_is_no_colour() => Assert.True(Read(Frame(colour: -1)).Colour.IsNone);

    [Fact]
    public void A_paint_index_is_the_colour() => Assert.Equal(Option.Some(Relays.Colour(5)), Read(Frame(colour: 5)).Colour);

    [Fact]
    public void An_empty_custom_name_is_no_name() => Assert.True(Read(Frame(name: "")).Name.IsNone);

    [Fact]
    public void A_custom_name_is_the_name() => Assert.Equal(Option.Some(Relays.Name("Pump 1")), Read(Frame(name: "Pump 1")).Name);

    [Fact]
    public void A_structure_with_no_setting_has_none() => Assert.True(Read(Frame()).Setting.IsNone);

    [Fact]
    public void A_labeller_setting_is_the_setting() =>
        Assert.Equal(Option.Some(Relays.Setting(42.5)), Read(Frame() with { Setting = Option.Some(42.5) }).Setting);

    [Fact]
    public void A_setting_that_is_not_finite_is_none() =>
        Assert.True(Read(Frame() with { Setting = Option.Some(double.NaN) }).Setting.IsNone);

    [Fact]
    public void A_rotation_that_is_no_cube_turn_is_refused_with_a_reason()
    {
        var s = (float)Math.Sin(Math.PI / 8);
        var tilted = Frame() with { Rotation = new GameRotation(0, s, 0, (float)Math.Cos(Math.PI / 8)) };
        Assert.True(StructureReading.Read(tilted).TryGetError(out var why));
        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
    }

    private static StructureInfo Read(GameStructure game) =>
        StructureReading.Read(game).TryGet(out var info) ? info : throw new InvalidOperationException("refused");
}

public sealed class GameTextTests
{
    [Theory]
    [InlineData("Oxygen", "oxygen")]
    [InlineData("CarbonDioxide", "carbon-dioxide")]
    [InlineData("LiquidCarbonDioxide", "liquid-carbon-dioxide")]
    [InlineData("HydrochloricAcid", "hydrochloric-acid")]
    public void Game_names_become_tokens(string game, string token) => Assert.Equal(token, GameText.Token(game));

    [Theory]
    [InlineData("Placement is blocked by <color=green>Steel Frame</color>", "Placement is blocked by Steel Frame")]
    [InlineData("Placement requires support", "Placement requires support")]
    [InlineData("<b>bold</b> and <i>it</i>", "bold and it")]
    [InlineData("pressure < 5 kPa", "pressure < 5 kPa")]
    [InlineData("a <3 b>", "a <3 b>")]
    public void Plain_text_has_no_tags(string game, string plain) => Assert.Equal(plain, GameText.Plain(game));

    [Fact]
    public void A_refusal_carries_the_game_message_without_tags()
    {
        var placement = new Placement(new PrefabKey(7), new Cell(1, 2, 3), Turn.None);
        var why = WorldReasons.Refused(placement, "Placement is blocked by <color=green>Steel Frame</color>");
        Assert.Equal(ReasonCodes.GameRefused, why.Code);
        Assert.Equal(Option.Some("Placement is blocked by Steel Frame"), why.GameMessage);
    }
}

internal static class Turns
{
    public static Turn Of(int index) => Turn.Create(index).TryGet(out var turn) ? turn : throw new ArgumentOutOfRangeException(nameof(index));
}
