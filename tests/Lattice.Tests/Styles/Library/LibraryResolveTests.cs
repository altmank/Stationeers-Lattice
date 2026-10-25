using System;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Styles;
using Lattice.Core.Styles.Library;
using Lattice.Tests.Board;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles.Library;

/// <summary>
/// The starter library on the real style resolution (<see cref="GhostStyles"/>), as a world's styles hold it: a thing in
/// each row's state resolves to that row's look, with the row named as the source of what it sets; the library's default
/// palette is the base; consumers' layers still win over it.
/// </summary>
public sealed class LibraryResolveTests
{
    private static readonly StarterLibrary Starter = new();
    private static readonly Cell At = new(10, 10, 10);
    private static readonly DraftsId Cursor = Must(DraftsId.Create(Mod, "cursor"));

    public static TheoryData<int> Rows()
    {
        var rows = new TheoryData<int>();
        foreach (LibraryRow row in Enum.GetValues(typeof(LibraryRow)))
            rows.Add((int)row);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void A_thing_in_the_rows_state_resolves_to_the_rows_look_on_the_real_styles(int row)
    {
        var bench = new StyleBench(true, Starter);
        var look = Starter.Look((LibraryRow)row);

        var resolved = LibraryBench.Opinions(Resolve(bench, look.Row));

        foreach (var (property, value) in LibraryBench.Opinions(look.Style))
            Assert.Equal(value, resolved[property]);
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void Explain_names_the_library_row_as_the_source_of_what_it_sets(int row)
    {
        var lookRow = (LibraryRow)row;
        if (lookRow is LibraryRow.Draft or LibraryRow.Pending or LibraryRow.SwapOld)
            return;
        var bench = new StyleBench(true, Starter);
        var ghost = Ghost(bench, lookRow);
        var index = Starter.Looks.Select((l, i) => (l, i)).Single(x => x.l.Row == lookRow).i;

        var story = bench.Story(ghost);

        foreach (var property in LibraryBench.Opinions(Starter.Look(lookRow).Style).Keys)
            Assert.Equal(new StyleSource.Library(index), story.Sources[Enum.Parse<StyleProperty>(property)]);
    }

    [Fact]
    public void The_library_default_palette_is_the_base_colour_of_a_planned_ghost()
    {
        var bench = new StyleBench(true, Starter);
        var ghost = bench.PlaceOne("StructureFrame", At);
        var planned = Starter.Default.Colours[LibraryPalette.Planned];

        var fill = bench.Look(ghost.Id).Fill;

        Assert.Equal((planned.R, planned.G, planned.B), (fill.R, fill.G, fill.B));
    }

    [Fact]
    public void A_consumers_theme_wins_over_the_library_and_the_library_shows_again_when_it_is_disabled()
    {
        var bench = new StyleBench(true, Starter);
        var ghost = Ghost(bench, LibraryRow.Fails);
        var calm = bench.Add(LayerOf("calm", StyleBand.Theme, 0, Rule(new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None), new Style
        {
            Effect = Option.Some<EffectLook>(new EffectLook.Steady()),
        })));
        bench.Settle();

        Assert.IsType<EffectLook.Steady>(bench.Look(ghost).Style.Effect.Reduce(new EffectLook.Pulse(default, default)));
        Assert.Equal(Option.Some(Pattern.Hatch), bench.Look(ghost).Style.Pattern);

        calm.Disable();
        bench.Settle();
        Assert.IsType<EffectLook.Pulse>(bench.Look(ghost).Style.Effect.Reduce(new EffectLook.Steady()));
    }

    [Fact]
    public void The_blueprint_example_theme_added_over_the_library_fills_a_passing_ghost_in_the_cursors_green()
    {
        var bench = new StyleBench(true, Starter);
        bench.Add(Starter.Blueprint);
        var ghost = Ghost(bench, LibraryRow.Next);

        var fill = bench.Look(ghost).Fill;

        Assert.Equal((0, 255, 0), (fill.R, fill.G, fill.B));
    }

    [Fact]
    public void A_modded_piece_gets_the_library_look()
    {
        var bench = new StyleBench(true, Starter);
        var modded = bench.PlaceOne(Modded, new Cell(5, 5, 5));
        var frame = bench.PlaceOne("StructureFrame", At);

        Assert.Equal(bench.Look(frame.Id).Style, bench.Look(modded.Id).Style);
    }

    [Fact]
    public void Styles_without_the_library_have_no_library_looks()
    {
        var bench = new StyleBench();
        var ghost = bench.PlaceOne("StructureFrame", At);

        Assert.DoesNotContain(bench.Story(ghost.Id).Sources.Values, s => s is StyleSource.Library);
    }

    /// <summary>The look of a thing in the row's state on fresh real styles with the library.</summary>
    internal static Style RealLook(LibraryRow row) => Resolve(new StyleBench(true, Starter), row);

    private static Style Resolve(StyleBench bench, LibraryRow row)
    {
        switch (row)
        {
            case LibraryRow.SwapOld:
                var swap = Ghost(bench, LibraryRow.Swap);
                return bench.Styles.LookOf(swap, PartKind.Existing).Reduce(() => throw new Xunit.Sdk.XunitException("no overlay")).Style;
            case LibraryRow.Draft:
            case LibraryRow.Pending:
                var handle = (DraftsHandle)Must(bench.Styles.Drafts(Cursor));
                handle.Show(ValueList.Of(bench.Draft("StructureFrame", At)));
                if (row == LibraryRow.Pending)
                    handle.Hand(new Submission.Sent(new Ticket(CommandId.New())));
                return bench.Styles.Look(handle.Shown[0].Signature).Style;
            default:
                return bench.Look(Ghost(bench, row)).Style;
        }
    }

    internal static Finish Painted(int colour) => Must(Finish.Create(Option.Some(Must(PaintColour.Create(colour))), Option<ThingName>.None, Option<Setting>.None));

    private static StructureRef Standing(StyleBench bench) =>
        new(new ReferenceId(7), bench.T.Catalogue.Named("StructureFrame").Key, new Cell(30, 10, 10));

    /// <summary>A ghost on the bench's board in the row's state.</summary>
    private static GhostId Ghost(StyleBench bench, LibraryRow row)
    {
        var standing = Standing(bench);
        var ghost = row switch
        {
            LibraryRow.Remove => bench.Place(TestBoard.Draft(new Intent.Remove(standing))).Added[0],
            LibraryRow.Swap => bench.Place(TestBoard.Draft(new Intent.Swap(standing, new Placement(standing.Piece, standing.At, default), Option<Finish>.None))).Added[0],
            LibraryRow.Finish => bench.Place(TestBoard.Draft(new Intent.FinishOnly(standing, Painted(2)))).Added[0],
            LibraryRow.Finished => bench.Place(TestBoard.Draft(new Intent.Build(new Placement(standing.Piece, At, default), Option.Some(Painted(2))))).Added[0],
            LibraryRow.Others => TestBoard.Applied(bench.T.Apply(new Command.Place(ValueList.Of(bench.Draft("StructureFrame", At))), TestBoard.Bob)).Added[0],
            _ => bench.PlaceOne("StructureFrame", At),
        };
        var id = ghost.Id;
        var rule = Must(RuleId.Create(Mod, "rule"));
        var builder = new ExecutorInfo(Must(ExecutorId.Create(Mod, "builder")), Must(DisplayName.Create("Builder")));
        switch (row)
        {
            case LibraryRow.Started:
                Must(bench.T.Board.MarkStarted(id, standing));
                break;
            case LibraryRow.Stale:
                Must(bench.T.Board.MarkStale(id, Reason.Of(ReasonCodes.NotFound, rule)));
                break;
            case LibraryRow.Next:
                bench.Verdicts.Raise((id, new Finding.Passes(id)));
                break;
            case LibraryRow.Waits or LibraryRow.WaitsHovered:
                bench.Verdicts.Raise((id, new Finding.WaitsFor(id, ValueList.Of(new GhostId(999)))));
                break;
            case LibraryRow.Fails or LibraryRow.FailsHovered:
                bench.Verdicts.Raise((id, new Finding.WillFail(id, Reason.Of(ReasonCodes.NotFound, rule))));
                break;
            case LibraryRow.Warns:
                bench.Verdicts.Raise((id, new Finding.Warns(id, Reason.Of(ReasonCodes.NotFound, rule))));
                break;
            case LibraryRow.Working:
                bench.Statuses.Set(id, new ExecutorReport(builder, new ExecutorStatus.Working(1, 3)));
                bench.T.Events.RaiseStatusChanged(id);
                break;
            case LibraryRow.Waiting:
                bench.Statuses.Set(id, new ExecutorReport(builder, new ExecutorStatus.Waiting(Reason.Of(ReasonCodes.NotFound, rule))));
                bench.T.Events.RaiseStatusChanged(id);
                break;
            case LibraryRow.Paused:
                bench.Statuses.Set(id, new ExecutorReport(builder, new ExecutorStatus.Paused()));
                bench.T.Events.RaiseStatusChanged(id);
                break;
            case LibraryRow.Selected:
                Must(bench.Styles.Set(LibrarySets.Selected)).Put(id);
                break;
        }

        if (row is LibraryRow.Hovered or LibraryRow.WaitsHovered or LibraryRow.FailsHovered)
            Must(bench.Styles.Set(LibrarySets.Hovered)).Put(id);
        bench.Settle();
        return id;
    }
}
