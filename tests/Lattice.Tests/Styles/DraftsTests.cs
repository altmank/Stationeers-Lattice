using System;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.Core.Styles;
using static Lattice.Tests.Styles.StyleBench;

namespace Lattice.Tests.Styles;

/// <summary>
/// Drafts: raw drafts are drawn in the call as unchecked drafts; a preview replaces them with
/// its trial ghosts and findings; a hand-over never leaves a frame where neither the drafts nor the applied ghosts are
/// drawn, nor one where both are; refused drafts go at once; the world's end drops them.
/// </summary>
public sealed class DraftsTests
{
    private static readonly DraftsId Cursor = Must(DraftsId.Create(Mod, "cursor"));

    private static StyleLayerDescriptor DraftLooks() => LayerOf("drafts", StyleBand.Theme, 0,
        Rule(new StyleMatch.Draft(), new Style { Pattern = Option.Some(Pattern.Hatch) }),
        Rule(new StyleMatch.Finding(FindingKind.Unchecked, Option<ReasonCode>.None), new Style { Icon = Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Waiting)) }),
        Rule(new StyleMatch.Finding(FindingKind.WillFail, Option<ReasonCode>.None), new Style { Icon = Option.Some<IconLook>(new IconLook.Shown(LibraryIcons.Fails)) }),
        Rule(new StyleMatch.Part(PartKind.Existing), new Style { Dim = Option.Some(Must(Fraction.Create(0.5f))) }),
        Rule(new StyleMatch.Pending(), new Style { Pattern = Option.Some(Pattern.Dots) }));

    private static Style LookOf(StyleBench bench, Thing thing) => bench.Styles.Look(thing.Signature).Style;

    private static DraftsHandle Handle(StyleBench bench) => (DraftsHandle)Must(bench.Styles.Drafts(Cursor));

    [Fact]
    public void Raw_drafts_are_drawn_in_the_call_as_unchecked_drafts_with_overlays_for_what_they_take_down()
    {
        var bench = new StyleBench();
        bench.Add(DraftLooks());
        var handle = Handle(bench);
        var built = new StructureRef(new ReferenceId(77), bench.T.Catalogue.Named("StructureFrame").Key, new Cell(50, 10, 10));

        handle.Show(ValueList.From(new[]
        {
            bench.Draft("StructureFrame", new Cell(10, 10, 10)),
            Lattice.Tests.Board.TestBoard.Draft(new Intent.Remove(built)),
        }));

        Assert.Equal(2, handle.Shown.Count);
        var placement = handle.Shown[0];
        Assert.Equal(PartKind.Placement, placement.Part);
        Assert.NotNull(placement.Batch);
        Assert.Equal(Pattern.Hatch, LookOf(bench, placement).Pattern.Reduce(Pattern.None));
        Assert.Equal(new IconLook.Shown(LibraryIcons.Waiting), LookOf(bench, placement).Icon.Reduce(new IconLook.None()));
        var overlay = handle.Shown[1];
        Assert.Equal(PartKind.Existing, overlay.Part);
        Assert.Equal(0.5f, LookOf(bench, overlay).Dim.Reduce(Fraction.One).Value, 3);
    }

    [Fact]
    public void A_preview_replaces_the_raw_drafts_with_its_trial_ghosts_and_their_findings()
    {
        var bench = new StyleBench();
        bench.Add(DraftLooks());
        var handle = Handle(bench);
        handle.Show(ValueList.Of(bench.Draft("StructureFrame", new Cell(10, 10, 10))));
        var raw = handle.Shown[0];
        var trial = Lattice.Tests.Board.TestBoard.Applied(new Lattice.Tests.Board.TestBoard().Place(bench.Draft("StructureFrame", new Cell(10, 10, 10)))).Added[0];

        handle.Show(PreviewOf(trial, new Finding.WillFail(trial.Id, Reason.Of(ReasonCodes.NotFound, Must(RuleId.Create(Mod, "rule"))))));

        Assert.True(raw.Gone);
        var shown = Assert.Single(handle.Shown);
        Assert.Same(trial, shown.Record);
        Assert.Equal(new IconLook.Shown(LibraryIcons.Fails), LookOf(bench, shown).Icon.Reduce(new IconLook.None()));
    }

    [Fact]
    public void On_the_host_an_applied_hand_over_draws_the_draft_or_the_ghost_in_every_frame_never_neither_nor_both()
    {
        var bench = new StyleBench();
        var handle = Handle(bench);
        var draft = bench.Draft("StructureFrame", new Cell(10, 10, 10));
        handle.Show(ValueList.Of(draft));
        bench.Frame();

        var outcome = bench.T.Apply(new Command.Place(ValueList.Of(draft)));
        var ghost = Lattice.Tests.Board.TestBoard.Applied(outcome).Added[0];
        handle.Hand(new Submission.Answered(new Ticket(CommandId.New()), outcome));

        ExactlyOneDrawnUntilSettled(bench, handle, ghost.Id);
        Assert.False(handle.ShowsAnything);
        Assert.True(bench.Styles.Draws(ghost.Id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void On_a_client_sent_drafts_turn_pending_then_hand_over_without_a_gap_whichever_event_comes_first(bool answerFirst)
    {
        var bench = new StyleBench();
        bench.Add(DraftLooks());
        var handle = Handle(bench);
        var draft = bench.Draft("StructureFrame", new Cell(10, 10, 10));
        handle.Show(ValueList.Of(draft));
        var ticket = new Ticket(CommandId.New());

        handle.Hand(new Submission.Sent(ticket));
        Assert.Equal(Pattern.Dots, LookOf(bench, handle.Shown[0]).Pattern.Reduce(Pattern.None));
        bench.Frame();
        Assert.True(handle.ShowsAnything);

        Outcome? outcome = null;
        if (answerFirst)
            bench.T.Events.Queue(new Later(() => new AnsweredEvent(ticket, outcome!)));
        outcome = bench.T.Apply(new Command.Place(ValueList.Of(draft)));
        if (!answerFirst)
            bench.T.Events.Queue(new AnsweredEvent(ticket, outcome));
        var ghost = Lattice.Tests.Board.TestBoard.Applied(outcome).Added[0];

        ExactlyOneDrawnUntilSettled(bench, handle, ghost.Id);
        Assert.False(handle.ShowsAnything);
    }

    [Fact]
    public void Refused_drafts_are_dropped_at_once_and_sent_ones_when_their_ticket_is_refused()
    {
        var bench = new StyleBench();
        var handle = Handle(bench);
        var refused = new Outcome.Refused(ValueList.Of(new Refusal(Reason.Of(ReasonCodes.NotFound, Must(RuleId.Create(Mod, "rule"))), ValueList<GhostId>.Empty)));
        handle.Show(ValueList.Of(bench.Draft("StructureFrame", new Cell(10, 10, 10))));

        handle.Hand(new Submission.Answered(new Ticket(CommandId.New()), refused));
        Assert.False(handle.ShowsAnything);

        var ticket = new Ticket(CommandId.New());
        handle.Show(ValueList.Of(bench.Draft("StructureFrame", new Cell(10, 10, 10))));
        handle.Hand(new Submission.Sent(ticket));
        bench.T.Events.Queue(new AnsweredEvent(ticket, refused));
        bench.T.Events.Flush();
        Assert.False(handle.ShowsAnything);
    }

    [Fact]
    public void Showing_again_while_handed_over_keeps_the_new_drafts_when_the_old_change_set_lands()
    {
        var bench = new StyleBench();
        var handle = Handle(bench);
        var first = bench.Draft("StructureFrame", new Cell(10, 10, 10));
        handle.Show(ValueList.Of(first));
        var outcome = bench.T.Apply(new Command.Place(ValueList.Of(first)));
        handle.Hand(new Submission.Answered(new Ticket(CommandId.New()), outcome));

        handle.Show(ValueList.Of(bench.Draft("StructureFrame", new Cell(30, 10, 10))));
        bench.Settle();

        Assert.True(handle.ShowsAnything);
    }

    [Fact]
    public void The_worlds_end_drops_every_draft_and_disposing_frees_the_id()
    {
        var bench = new StyleBench();
        var handle = Handle(bench);
        handle.Show(ValueList.Of(bench.Draft("StructureFrame", new Cell(10, 10, 10))));

        bench.Styles.End();

        Assert.False(handle.ShowsAnything);
        var other = new StyleBench();
        var held = Handle(other);
        held.Dispose();
        Assert.NotSame(held, Handle(other));
    }

    private static void ExactlyOneDrawnUntilSettled(StyleBench bench, DraftsHandle handle, GhostId ghost)
    {
        for (var frame = 0; frame < 50; frame++)
        {
            Assert.True(handle.ShowsAnything ^ bench.Styles.Draws(ghost), $"frame {frame}: draft {handle.ShowsAnything}, ghost {bench.Styles.Draws(ghost)}");
            if (!handle.ShowsAnything && !bench.Styles.Busy && bench.T.Events.Waiting == 0)
                return;
            bench.Frame();
        }

        throw new Xunit.Sdk.XunitException("the hand-over never finished");
    }

    private static Api.Preview PreviewOf(Ghost trial, Finding finding) => new(
        new ChangeSet(new ChangeSetId(9), new CommandId(Guid.NewGuid()), Lattice.Tests.Board.TestBoard.Alice, new Revision(1), DateTimeOffset.UnixEpoch,
            ValueList.Of(trial), ValueList<GhostChange>.Empty, ValueList<GhostRemoval>.Empty, ValueList<Plan>.Empty, ValueList<PlanChange>.Empty, ValueList<PlanId>.Empty),
        ValueList.Of(finding),
        new Bill(ValueList<ItemQuantity>.Empty, ValueList<ItemQuantity>.Empty, ValueList<ItemDelta>.Empty, ValueSet<ItemKey>.Empty),
        new AnalysisReport(ValueList<NetworkEffect>.Empty, ValueList<RoomEffect>.Empty, ValueList<GasEffect>.Empty, ValueList<AnalyzerFact>.Empty),
        Option<RunReport>.None);

    /// <summary>An event made when it is raised, so it can name an outcome not known when it was queued.</summary>
    private sealed class Later(Func<QueuedEvent> make) : QueuedEvent
    {
        public override void Raise(BoardEvents events) => make().Raise(events);
    }
}
