using Lattice.Api;
using Lattice.Tests.Board;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// <see cref="StyleMatch.Painted"/> holds for a ghost whose finish sets a paint colour, <see cref="StyleMatch.Named"/> for
/// one whose finish sets a name; a labeller setting alone is neither. A finish change restyles the ghost once.
/// </summary>
public sealed class FinishMatchTests
{
    private static readonly Cell At = new(10, 10, 10);

    private static readonly Style PaintedLook = new() { Pattern = Option.Some(Pattern.Hatch) };

    private static readonly Style NamedLook = new() { Fill = Option.Some(FillMode.Wire) };

    private static Finish FinishOf(int? colour = null, string? name = null, double? setting = null) => Must(Finish.Create(
        colour is { } c ? Option.Some(Must(PaintColour.Create(c))) : Option<PaintColour>.None,
        name is { } n ? Option.Some(Must(ThingName.Create(n))) : Option<ThingName>.None,
        setting is { } s ? Option.Some(Must(Setting.Create(s))) : Option<Setting>.None));

    private static StyleBench Bench()
    {
        var bench = new StyleBench();
        bench.Add(LayerOf("finishes", StyleBand.Theme, 0,
            Rule(new StyleMatch.Painted(), PaintedLook),
            Rule(new StyleMatch.Named(), NamedLook)));
        return bench;
    }

    private static GhostId Built(StyleBench bench, Option<Finish> finish) =>
        bench.Place(TestBoard.Draft(new Intent.Build(new Placement(bench.T.Catalogue.Named("StructureFrame").Key, At, default), finish))).Added[0].Id;

    private static (bool Painted, bool Named) Matched(StyleBench bench, GhostId ghost)
    {
        var style = bench.Look(ghost).Style;
        return (style.Pattern.Reduce(Pattern.None) == Pattern.Hatch, style.Fill.Reduce(FillMode.Ghost) == FillMode.Wire);
    }

    [Theory]
    [InlineData(3, null, null, true, false)]
    [InlineData(null, "Filter 1", null, false, true)]
    [InlineData(3, "Filter 1", null, true, true)]
    [InlineData(null, null, 2.0, false, false)]
    [InlineData(null, "Filter 1", 2.0, false, true)]
    public void A_finish_matches_painted_and_named_by_what_it_sets(int? colour, string? name, double? setting, bool painted, bool named)
    {
        var bench = Bench();

        var ghost = Built(bench, Option.Some(FinishOf(colour, name, setting)));

        Assert.Equal((painted, named), Matched(bench, ghost));
    }

    [Fact]
    public void A_ghost_with_no_finish_is_neither()
    {
        var bench = Bench();

        Assert.Equal((false, false), Matched(bench, Built(bench, Option<Finish>.None)));
    }

    [Fact]
    public void A_finish_only_ghost_matches_by_its_finish()
    {
        var bench = Bench();
        var standing = new StructureRef(new ReferenceId(7), bench.T.Catalogue.Named("StructureFrame").Key, new Cell(30, 10, 10));

        var ghost = bench.Place(TestBoard.Draft(new Intent.FinishOnly(standing, FinishOf(name: "Pump")))).Added[0].Id;

        Assert.Equal((false, true), Matched(bench, ghost));
    }

    [Fact]
    public void A_built_structure_is_neither()
    {
        var bench = Bench();
        var frame = new StructureRef(new ReferenceId(1), bench.T.Catalogue.Named("StructureFrame").Key, At);

        Assert.False(bench.Styles.Matches(new StyleMatch.Painted(), frame));
        Assert.False(bench.Styles.Matches(new StyleMatch.Named(), frame));
        Assert.True(bench.Styles.Matches(new StyleMatch.Not(new StyleMatch.Named()), frame));
    }

    [Fact]
    public void A_finish_change_restyles_the_ghost_once()
    {
        var bench = Bench();
        var ghost = Built(bench, Option.Some(FinishOf(colour: 3)));
        Assert.Equal((true, false), Matched(bench, ghost));
        var resolves = bench.Styles.Resolves;

        TestBoard.Applied(bench.T.Apply(new Command.Change(TestBoard.Ids(ghost), new GhostEdit.SetFinish(Option.Some(FinishOf(name: "Filter 1"))), Option<Revision>.None)));
        bench.Settle();

        Assert.Equal((false, true), Matched(bench, ghost));
        Assert.Equal(1, bench.Styles.Resolves - resolves);
    }
}
