using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Tests.Runs;
using Lattice.Tests.Styles.Library;

namespace Lattice.Tests.EntryPoint;

/// <summary>
/// The world's paint colours (<see cref="Lattice.Core.EntryPoint.WorldPorts.Paints"/>, the game's
/// <c>GameManager.CustomColors</c> in game) reach the styles: a layer that edges finish ghosts with
/// <see cref="Paint.FinishColour"/> shows the game's colour for the paint; with none known, the fallback token.
/// </summary>
[Collection(LatticeSystemCollection.Name)]
public sealed class PaintColoursTests
{
    private static readonly Rgba Teal = new(10, 120, 130, 255);

    [Fact]
    public void A_finish_ghost_shows_the_games_colour_for_its_paint()
    {
        using var bench = new WorldBench { Paints = new Paints(2, Teal) };

        Assert.Equal((Teal.R, Teal.G, Teal.B), Rgb(FinishEdge(bench, 2)));
    }

    [Fact]
    public void Without_the_games_colours_finish_paint_falls_back_to_its_token()
    {
        using var bench = new WorldBench();

        Assert.NotEqual((Teal.R, Teal.G, Teal.B), Rgb(FinishEdge(bench, 2)));
    }

    [Fact]
    public void A_paint_the_game_does_not_hold_falls_back_to_its_token()
    {
        using var bench = new WorldBench { Paints = new Paints(2, Teal) };
        var fallback = Rgb(FinishEdge(new WorldBench(), 5));

        Assert.Equal(fallback, Rgb(FinishEdge(bench, 5)));
    }

    // Game's request (paint colours): in a full world the library's passing edge hid the finish-only edge's paint.
    [Fact]
    public void A_validated_finish_only_ghost_keeps_its_paint_on_the_library_edge()
    {
        using var bench = new WorldBench { Paints = new Paints(2, Teal) };
        var standing = bench.Runs.Real("StructureFrame", new Cell(31, 11, 11));
        var world = bench.Start();
        var ghost = Assert.IsType<Outcome.Applied>(bench.Submit(new Command.Place(ValueList.Of(
            Lattice.Tests.Board.TestBoard.Draft(new Intent.FinishOnly(standing, LibraryResolveTests.Painted(2))))))).Changes.Added[0];
        bench.Settle();

        var report = world.Validation.Check(new Selector.Ids(ValueList.Of(ghost.Id))).Match(static a => a, static why => throw new Xunit.Sdk.XunitException(why.ToString()));
        var ready = Assert.IsType<Answer<ValidationReport>.Ready>(report);
        Assert.IsType<Finding.Passes>(Assert.Single(ready.Value.Findings));
        var edge = world.StyleModule!.LookOf(ghost.Id).Match(static l => l.Edge, static () => throw new Xunit.Sdk.XunitException("not drawn"));
        Assert.Equal((Teal.R, Teal.G, Teal.B), Rgb(edge));
    }

    private static Rgba FinishEdge(WorldBench bench, int paint)
    {
        var standing = bench.Runs.Real("StructureFrame", new Cell(31, 11, 11));
        var world = bench.Start();
        var mod = Must(ModId.Create("paints"));
        var finishEdge = new Style
        {
            Edge = Option.Some<EdgeLook>(new EdgeLook.Line(LineStyle.Solid, Must(Pixels.Create(2f)), new Paint.FinishColour(LibraryPalette.Planned))),
        };
        Must(world.Styles.Add(new StyleLayerDescriptor(Must(StyleLayerId.Create(mod, "finish-edge")), StyleBand.Theme, 0,
            ValueList.Of(new StyleRule(new StyleMatch.Finishes(), finishEdge)))));
        var ghost = Assert.IsType<Outcome.Applied>(bench.Submit(new Command.Place(ValueList.Of(
            Lattice.Tests.Board.TestBoard.Draft(new Intent.FinishOnly(standing, LibraryResolveTests.Painted(paint))))))).Changes.Added[0];
        bench.Settle();
        var edge = world.StyleModule!.LookOf(ghost.Id).Match(static l => l.Edge, static () => throw new Xunit.Sdk.XunitException("not drawn"));
        bench.Dispose();
        return edge;
    }

    private static T Must<T>(Result<T, Reason> result) where T : notnull =>
        result.Match(static v => v, static why => throw new Xunit.Sdk.XunitException(why.ToString()));

    private static (byte R, byte G, byte B) Rgb(Rgba colour) => (colour.R, colour.G, colour.B);

    private sealed class Paints(int index, Rgba colour) : IPaintColours
    {
        private readonly Dictionary<int, Rgba> _colours = new() { [index] = colour };

        public Option<Rgba> Colour(PaintColour paint) =>
            _colours.TryGetValue(paint.Index, out var held) ? Option.Some(held) : Option<Rgba>.None;
    }
}
