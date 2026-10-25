using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Core.Styles.Library;
using Lattice.Testing;
using Lattice.Tests.Board;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles.Library;

/// <summary>
/// What consumers reach of the library: a palette laid on a library palette (<see cref="Palette.Base"/>), the ready-made
/// layers (<see cref="LibraryLayers"/>), the finish facet (<see cref="StyleMatch.Finishes"/>) and the finish's paint
/// colour (<see cref="Paint.FinishColour"/>).
/// </summary>
public sealed class LibraryReachTests
{
    private static readonly StarterLibrary Starter = new();
    private static readonly Cell At = new(10, 10, 10);
    private static readonly Rgba Paint2 = new(10, 20, 30, 255);

    [Fact]
    public void A_theme_on_the_colour_safe_base_takes_its_colours_and_changes_only_its_own_tokens()
    {
        var bench = new StyleBench(true, Starter);
        var mine = new Rgba(200, 60, 60, 255);
        bench.Add(LayerOf("safe", StyleBand.Theme, 0) with
        {
            Palette = Option.Some(new Palette(Must(PaletteId.Create(Mod, "safe")), ValueMap.Of(LibraryPalette.Fail, mine)) { Base = Option.Some(LibraryPalettes.ColourSafe) }),
        });
        var planned = bench.PlaceOne("StructureFrame", At);
        var failing = bench.PlaceOne("StructureFrame", new Cell(30, 10, 10));
        bench.Verdicts.Raise((failing.Id, new Finding.WillFail(failing.Id, Reason.Of(ReasonCodes.NotFound, Must(RuleId.Create(Mod, "rule"))))));
        bench.Settle();

        Assert.Equal(Rgb(Starter.ColourSafe.Colours[LibraryPalette.Planned]), Rgb(bench.Look(planned.Id).Fill));
        Assert.Equal(Rgb(mine), Rgb(bench.Look(failing.Id).Edge));
    }

    [Fact]
    public void A_palette_base_that_is_not_a_library_palette_is_refused_by_the_styles_and_by_the_test_host_fake()
    {
        var unknown = Must(PaletteId.Create(Mod, "nowhere"));
        var layer = LayerOf("odd", StyleBand.Theme, 0) with
        {
            Palette = Option.Some(new Palette(Must(PaletteId.Create(Mod, "odd")), ValueMap<PaletteKey, Rgba>.Empty) { Base = Option.Some(unknown) }),
        };

        Assert.True(new StyleBench(true, Starter).Styles.Add(layer).TryGetError(out var real));
        Assert.True(new FakeGhostStyles().Add(layer).TryGetError(out var fake));
        Assert.Equal(ReasonCodes.InvalidValue, real.Code);
        Assert.Equal(ReasonCodes.InvalidValue, fake.Code);
    }

    [Fact]
    public void The_ready_made_layers_are_new_values_on_each_read_equal_to_the_ones_before()
    {
        Assert.NotSame(LibraryLayers.Blueprint, LibraryLayers.Blueprint);
        Assert.Equal(LibraryLayers.Blueprint, LibraryLayers.Blueprint);
        Assert.Equal(LibraryLayers.Reasons, LibraryLayers.Reasons);
    }

    [Fact]
    public void A_ready_made_layer_can_be_added_once_per_world()
    {
        var bench = new StyleBench(true, Starter);

        bench.Add(LibraryLayers.StepNumbers);

        Assert.True(bench.Styles.Add(LibraryLayers.StepNumbers).TryGetError(out var again));
        Assert.Equal(ReasonCodes.AlreadyRegistered, again.Code);
    }

    [Fact]
    public void Finishes_holds_for_a_finish_only_ghost_and_a_build_with_a_finish_and_not_for_a_plain_build_or_a_structure()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("finishes", StyleBand.Meaning, 0, Rule(new StyleMatch.Finishes(), new Style { Pattern = Option.Some(Pattern.Dots) })));
        var frame = bench.T.Catalogue.Named("StructureFrame").Key;
        var standing = new StructureRef(new ReferenceId(7), frame, new Cell(50, 10, 10));
        var plain = bench.PlaceOne("StructureFrame", At);
        var finished = bench.Place(TestBoard.Draft(new Intent.Build(new Placement(frame, new Cell(30, 10, 10), default), Option.Some(LibraryResolveTests.Painted(2))))).Added[0];
        var only = bench.Place(TestBoard.Draft(new Intent.FinishOnly(standing, LibraryResolveTests.Painted(2)))).Added[0];

        Assert.Equal(Pattern.None, bench.Look(plain.Id).Style.Pattern.Reduce(Pattern.None));
        Assert.Equal(Option.Some(Pattern.Dots), bench.Look(finished.Id).Style.Pattern);
        Assert.Equal(Option.Some(Pattern.Dots), bench.Look(only.Id).Style.Pattern);
        Assert.False(bench.Styles.Matches(new StyleMatch.Finishes(), standing));
    }

    [Fact]
    public void A_finish_only_ghost_is_edged_in_the_games_colour_for_its_paint()
    {
        var bench = new StyleBench(true, Starter, new TestPaints(), []);
        var standing = new StructureRef(new ReferenceId(7), bench.T.Catalogue.Named("StructureFrame").Key, new Cell(50, 10, 10));

        var only = bench.Place(TestBoard.Draft(new Intent.FinishOnly(standing, LibraryResolveTests.Painted(2)))).Added[0];

        Assert.Equal(Rgb(Paint2), Rgb(bench.Look(only.Id).Edge));
    }

    [Fact]
    public void Two_finishes_in_different_paints_look_apart_and_one_the_seat_has_no_colour_for_takes_the_fallback()
    {
        var bench = new StyleBench(true, Starter, new TestPaints(), []);
        var frame = bench.T.Catalogue.Named("StructureFrame").Key;

        var two = bench.Place(TestBoard.Draft(new Intent.FinishOnly(new StructureRef(new ReferenceId(7), frame, new Cell(50, 10, 10)), LibraryResolveTests.Painted(2)))).Added[0];
        var five = bench.Place(TestBoard.Draft(new Intent.FinishOnly(new StructureRef(new ReferenceId(8), frame, new Cell(70, 10, 10)), LibraryResolveTests.Painted(5)))).Added[0];

        Assert.Equal(Rgb(Paint2), Rgb(bench.Look(two.Id).Edge));
        Assert.Equal(Rgb(Starter.Default.Colours[LibraryPalette.Planned]), Rgb(bench.Look(five.Id).Edge));
    }

    [Fact]
    public void Without_the_games_colours_finish_paint_shows_its_fallback_token()
    {
        var bench = new StyleBench(true, Starter);
        var standing = new StructureRef(new ReferenceId(7), bench.T.Catalogue.Named("StructureFrame").Key, new Cell(50, 10, 10));

        var only = bench.Place(TestBoard.Draft(new Intent.FinishOnly(standing, LibraryResolveTests.Painted(2)))).Added[0];

        Assert.Equal(Rgb(Starter.Default.Colours[LibraryPalette.Planned]), Rgb(bench.Look(only.Id).Edge));
    }

    private static (byte R, byte G, byte B) Rgb(Rgba colour) => (colour.R, colour.G, colour.B);

    /// <summary>The game's colour for paint 2 only.</summary>
    private sealed class TestPaints : IPaintColours
    {
        private readonly Dictionary<int, Rgba> _colours = new() { [2] = Paint2 };

        public Option<Rgba> Colour(PaintColour paint) =>
            _colours.TryGetValue(paint.Index, out var colour) ? Option.Some(colour) : Option<Rgba>.None;
    }
}
