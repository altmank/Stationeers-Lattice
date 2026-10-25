using System;
using System.Collections.Generic;
using System.Diagnostics;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit.Allocations;
using Xunit.Abstractions;
using static Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Board;

public sealed class EventAndReadTests(ITestOutputHelper output)
{
    private static readonly Cell At = new(100, 100, 100);

    [Fact]
    public void One_changed_per_command_none_before_the_flush_all_in_revision_order()
    {
        var t = new TestBoard();
        var raised = new List<Revision>();
        t.Events.Changed += c => raised.Add(c.Revision);

        t.Placed(t.Draft("StructureCableStraight", At));
        t.Placed(t.Draft("StructureCableStraight", new Cell(200, 100, 100)));
        Assert.Empty(raised);

        t.Events.Flush();

        Assert.Equal([new Revision(1), new Revision(2)], raised);
    }

    [Fact]
    public void A_command_applied_from_a_handler_is_raised_in_the_next_flush()
    {
        var t = new TestBoard();
        var raised = new List<Revision>();
        var placedFromHandler = false;
        t.Events.Changed += c =>
        {
            raised.Add(c.Revision);
            if (placedFromHandler)
                return;
            placedFromHandler = true;
            t.Placed(t.Draft("StructureCableStraight", new Cell(200, 100, 100)));
        };
        t.Placed(t.Draft("StructureCableStraight", At));

        t.Events.Flush();
        Assert.Equal([new Revision(1)], raised);

        t.Events.Flush();
        Assert.Equal([new Revision(1), new Revision(2)], raised);
    }

    [Fact]
    public void Unsubscribing_during_a_raise_is_safe_and_takes_effect_next_raise()
    {
        var t = new TestBoard();
        var calls = new List<string>();
        Action<ChangeSet>? second = null;
        Action<ChangeSet> first = _ =>
        {
            calls.Add("first");
            t.Events.Changed -= second;
        };
        second = _ => calls.Add("second");
        t.Events.Changed += first;
        t.Events.Changed += second;
        t.Placed(t.Draft("StructureCableStraight", At));
        t.Placed(t.Draft("StructureCableStraight", new Cell(200, 100, 100)));

        t.Events.Flush();

        Assert.Equal(["first", "second", "first"], calls);
    }

    [Fact]
    public void A_handler_that_throws_leaves_the_flush_and_the_rest_raise_next_time()
    {
        var t = new TestBoard();
        var raised = new List<Revision>();
        t.Events.Changed += c =>
        {
            raised.Add(c.Revision);
            if (c.Revision == new Revision(1))
                throw new InvalidOperationException("handler bug");
        };
        t.Placed(t.Draft("StructureCableStraight", At));
        t.Placed(t.Draft("StructureCableStraight", new Cell(200, 100, 100)));

        Assert.Throws<InvalidOperationException>(t.Events.Flush);
        t.Events.Flush();

        Assert.Equal([new Revision(1), new Revision(2)], raised);
    }

    [Fact]
    public void Raising_queued_events_allocates_nothing()
    {
        var t = new TestBoard();
        var count = 0;
        t.Events.Changed += _ => count++;
        var queued = new ChangedEvent(t.Placed(t.Draft("StructureCableStraight", At)));
        t.Events.Flush();

        Allocations.AssertNone(() =>
        {
            t.Events.Queue(queued);
            t.Events.Flush();
        }, "queue and flush");
        Assert.True(count > 100);
    }

    [Fact]
    public void Reads_on_an_unchanged_board_allocate_nothing()
    {
        var t = new TestBoard();
        var ghost = Assert.Single(t.Placed(t.Draft("StructureCableStraight", At)).Added);
        var structure = new StructureRef(new ReferenceId(5), t.Catalogue.Named("StructureFrameIron").Key, new Cell(10, 10, 10));
        t.Placed(Draft(new Intent.Remove(structure)));
        var plans = new PlansView(t.Board, Alice);
        var selector = new Selector.InPlan(GhostBoard.DefaultPlan);
        var view = t.View;

        Allocations.AssertNone(() => view.Find(ghost.Id), "Find");
        Allocations.AssertNone(() => view.At(At), "At");
        Allocations.AssertNone(() => view.On(structure), "On");
        Allocations.AssertNone(() => view.Select(selector), "Select");
        Allocations.AssertNone(() => _ = plans.All, "Plans.All");
        Allocations.AssertNone(() => view.As(Bob).At(At), "As(viewer).At");
    }

    [Fact]
    public void Reads_through_an_installed_policy_allocate_nothing_on_an_unchanged_board()
    {
        var t = new TestBoard();
        t.Placed(t.Draft("StructureCableStraight", At));
        t.Policies.Install(new AccessPolicy((_, _) => true, (_, _) => true, _ => Result<Unit, Reason>.Ok(Unit.Value)));
        var view = t.View;

        Allocations.AssertNone(() => view.At(At), "At with a policy");
    }

    [Fact]
    public void A_hundred_thousand_ghosts_are_placed_and_found_by_cell()
    {
        const int count = 100_000;
        var t = new TestBoard();
        var drafts = new GhostDraft[count];
        for (var i = 0; i < count; i++)
            drafts[i] = t.Draft("StructureCableStraight", new Cell(5 * (i % 100), 5 * (i / 100 % 100), 5 * (i / 10_000)));

        var place = Stopwatch.StartNew();
        var changes = t.Placed(drafts);
        place.Stop();

        var find = Stopwatch.StartNew();
        var found = 0;
        for (var i = 0; i < count; i++)
            found += t.View.At(new Cell(5 * (i % 100), 5 * (i / 100 % 100), 5 * (i / 10_000))).Count;
        find.Stop();

        Assert.Equal(count, changes.Added.Count);
        Assert.Equal(count, found);
        output.WriteLine($"{count} ghosts: place {place.ElapsedMilliseconds} ms, look up every cell {find.ElapsedMilliseconds} ms");
    }
}
