using System;
using System.Collections.Generic;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Ports;
using Lattice.Core.Slices;
using Lattice.Core.Support;
using Lattice.Core.Validation;
using Lattice.TestKit;
using static Lattice.Tests.Validation.ValidationBench;

namespace Lattice.Tests.Validation;

/// <summary>Re-checking only near a change and its dependants, world changes, the reconciler, faults.</summary>
public sealed class RecheckTests
{
    private const int Side = 10;
    private const int Tall = 10;

    // 1,000 modded frames in 100 columns of 10, each frame resting on the one below; columns three large cells apart.
    private static (ValidationBench Bench, GhostId[,,] Frames) Columns()
    {
        var bench = new ValidationBench().Start();
        var frames = new GhostId[Side, Tall, Side];
        var pieces = new List<(string, Cell)>();
        for (var i = 0; i < Side; i++)
            for (var k = 0; k < Tall; k++)
                for (var j = 0; j < Side; j++)
                    pieces.Add(("ModStackFrame", Large(3 * i, k, 3 * j)));
        var ids = bench.Ghosts(pieces.ToArray());
        var n = 0;
        for (var i = 0; i < Side; i++)
            for (var k = 0; k < Tall; k++)
                for (var j = 0; j < Side; j++)
                    frames[i, k, j] = ids[n++];
        bench.Pump();
        bench.Runs.Clear();
        return (bench, frames);
    }

    private static HashSet<GhostId> Column(GhostId[,,] frames, int i, int j) =>
        Enumerable.Range(0, Tall).Select(k => frames[i, k, j]).ToHashSet();

    [Fact]
    public void Removing_one_ghost_rechecks_only_its_neighbours_and_dependants_counted()
    {
        var (bench, frames) = Columns();
        var removed = frames[5, 4, 5];

        bench.Apply(new Command.Remove(new Selector.Ids(ValueList.Of(removed))));
        bench.Pump();

        var rechecked = bench.Runs.SelectMany(r => r.Rechecked).ToHashSet();
        var column = Column(frames, 5, 5);
        Assert.Contains(frames[5, 5, 5], rechecked);
        Assert.Subset(column, rechecked);
        Assert.True(bench.Runs.Sum(r => r.Visited) < 200, $"visited {bench.Runs.Sum(r => r.Visited)} reader entries");
        Assert.Equal(1000 - 1, bench.Runs[^1].Ghosts);
        Assert.IsType<Finding.Passes>(Verdict(bench.Report(), frames[5, 5, 5]));
    }

    [Fact]
    public void A_structure_built_beside_one_column_rechecks_only_that_column()
    {
        var (bench, frames) = Columns();

        bench.Built("StructureFrameIron", Large(16, 0, 15));
        bench.Pump();

        var rechecked = bench.Runs.SelectMany(r => r.Rechecked).ToHashSet();
        Assert.NotEmpty(rechecked);
        Assert.Subset(Column(frames, 5, 5), rechecked);
    }

    [Fact]
    public void After_a_world_change_the_last_report_answers_until_the_recheck_lands_then_answers_ready_is_raised_again()
    {
        var bench = new ValidationBench();
        var below = bench.Built("StructureFrameIron", Large(0, 0, 0));
        bench.Placement.Refuse(p => p.At == Large(0, 1, 0) && bench.World.At(Large(0, 0, 0)).Count == 0, ReasonCodes.GameRefused, "No frame");
        bench.Start();
        var upper = bench.Ghost("ModStackFrame", Large(0, 1, 0));
        bench.Pump();
        Assert.IsType<Finding.Passes>(Verdict(bench.Report(), upper));
        bench.Events.Flush();
        var raised = new List<Revision>();
        bench.Events.AnswersReady += raised.Add;

        bench.World.Remove(below.Id);
        var trailing = bench.Report();
        bench.Pump();
        bench.Events.Flush();

        Assert.IsType<Finding.Passes>(Verdict(trailing, upper));
        Assert.IsType<Finding.WillFail>(Verdict(bench.Report(), upper));
        Assert.Equal(new[] { bench.Board.Revision }, raised);
    }

    [Fact]
    public void The_reconciler_finds_a_game_answer_that_changed_with_no_signal_and_rechecks_only_its_readers()
    {
        var bench = new ValidationBench();
        var refuse = false;
        bench.Placement.Rule(p => refuse && p.At == Large(0, 0, 0)
            ? Option.Some(new Reason(ReasonCodes.GameRefused, ValueMap<string, string>.Empty, Option.Some("Terrain"), TestKitIds.PlacementCheck))
            : Option<Reason>.None);
        bench.Start();
        var changing = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        var far = bench.Ghost("StructureFrameIron", Large(9, 0, 0));
        bench.Pump();

        refuse = true;
        bench.Pump();
        Assert.IsType<Finding.Passes>(Verdict(bench.Report(), changing));
        bench.Runs.Clear();
        bench.Clock.Advance(TimeSpan.FromSeconds(10));
        bench.Pump();

        Assert.IsType<Finding.WillFail>(Verdict(bench.Report(), changing));
        var rechecked = bench.Runs.SelectMany(r => r.Rechecked).ToHashSet();
        Assert.Contains(changing, rechecked);
        Assert.DoesNotContain(far, rechecked);
    }

    [Fact]
    public void A_sweep_spends_at_most_its_slice_a_frame_and_still_asks_every_held_game_fact_again()
    {
        var bench = new ValidationBench();
        var cost = TimeSpan.FromMilliseconds(0.1);
        bench.Placement.Rule(_ =>
        {
            bench.Clock.Advance(cost);
            return Option<Reason>.None;
        });
        bench.Start();
        bench.Ghosts(Enumerable.Range(0, 40).Select(i => ("StructureFrameIron", Large(2 * i, 0, 0))).ToArray());
        bench.Pump();
        var settled = bench.Placement.Calls.Count;
        var held = bench.Placement.Calls.Distinct().Count();

        bench.Clock.Advance(TimeSpan.FromSeconds(10));
        var frames = 0;
        do
        {
            bench.Frame();
            frames++;
            Assert.True(bench.Scheduler.Spent(WorkKind.Checking) <= ValidationParts.DefaultSweepSlice + cost, $"frame {frames}: {bench.Scheduler.Spent(WorkKind.Checking)}");
        }
        while (bench.Service.Busy && frames < 1000);

        Assert.False(bench.Service.Busy);
        Assert.Equal(held, bench.Placement.Calls.Count - settled);
        Assert.True(frames > 1, "the sweep took one frame");
    }

    [Fact]
    public void A_sweep_asks_as_many_facts_a_frame_as_its_slice_holds_and_does_not_start_again_before_its_period()
    {
        var bench = new ValidationBench();
        var cost = TimeSpan.FromMilliseconds(0.05);
        bench.Placement.Rule(_ =>
        {
            bench.Clock.Advance(cost);
            return Option<Reason>.None;
        });
        bench.Start();
        bench.Ghosts(Enumerable.Range(0, 40).Select(i => ("StructureFrameIron", Large(2 * i, 0, 0))).ToArray());
        bench.Pump();
        var held = bench.Placement.Calls.Distinct().Count();

        bench.Clock.Advance(TimeSpan.FromSeconds(10));
        var perFrame = new List<int>();
        do
        {
            var before = bench.Placement.Calls.Count;
            bench.Frame();
            perFrame.Add(bench.Placement.Calls.Count - before);
        }
        while (bench.Service.Busy && perFrame.Count < 1000);

        var slice = ValidationParts.DefaultSweepSlice;
        Assert.Equal(TimeSpan.TicksPerMillisecond / 4, slice.Ticks);
        Assert.Equal(held, perFrame.Sum());
        Assert.All(perFrame.Take(perFrame.Count - 1), asked => Assert.True(asked >= slice.Ticks / cost.Ticks, $"asked {asked} in a frame: {string.Join(", ", perFrame)}"));

        var after = bench.Placement.Calls.Count;
        for (var i = 0; i < 99; i++)
        {
            bench.Clock.Advance(TimeSpan.FromMilliseconds(99));
            bench.Frame();
        }

        Assert.Equal(after, bench.Placement.Calls.Count);
        Assert.False(bench.Service.Busy);
        bench.Clock.Advance(TimeSpan.FromMilliseconds(200));
        bench.Pump();
        Assert.Equal(after + held, bench.Placement.Calls.Count);
    }

    [Fact]
    public void A_sweep_asks_again_only_the_facts_ghosts_on_the_board_read()
    {
        var bench = new ValidationBench().Start();
        var ghosts = bench.Ghosts(Enumerable.Range(0, 40).Select(i => ("StructureFrameIron", Large(2 * i, 0, 0))).ToArray());
        bench.Pump();
        var kept = ghosts.Take(20).Select(id => Target(bench, id)).ToHashSet();

        bench.Apply(new Command.Remove(new Selector.Ids(ValueList.From(ghosts.Skip(20).ToList()))));
        bench.Pump();
        var settled = bench.Placement.Calls.Count;
        bench.Clock.Advance(TimeSpan.FromSeconds(10));
        bench.Pump();

        Assert.Equal(kept, bench.Placement.Calls.Skip(settled).ToHashSet());
    }

    [Fact]
    public void An_answer_that_comes_after_its_ghost_left_is_not_asked_again_by_a_sweep()
    {
        var bench = new ValidationBench().Start();
        var stays = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Pump();
        var leaves = bench.Ghost("StructureFrameIron", Large(6, 0, 0));
        var at = Target(bench, leaves);

        // The run asks for the new ghost's facts; the ghost leaves before the checking slices answer them.
        bench.Events.Flush();
        bench.Service.Frame();
        bench.Worker.RunAll();
        bench.Service.Frame();
        bench.Apply(new Command.Remove(new Selector.Ids(ValueList.Of(leaves))));
        bench.Scheduler.RunFrame();
        Assert.Contains(at, bench.Placement.Calls);
        bench.Pump();
        var settled = bench.Placement.Calls.Count;
        bench.Clock.Advance(TimeSpan.FromSeconds(10));
        bench.Pump();

        var swept = bench.Placement.Calls.Skip(settled).ToList();
        Assert.DoesNotContain(at, swept);
        Assert.Contains(Target(bench, stays), swept);
    }

    private static Placement Target(ValidationBench bench, GhostId ghost) =>
        ((Intent.Build)bench.Board.Index.Ghosts[ghost].Ghost.Intent).Target;

    [Fact]
    public void A_fault_on_the_worker_turns_validation_off_and_surfaces_once()
    {
        var bench = new ValidationBench(facts: pieces => new ThrowingFacts(ValidationBench.DefaultFacts(pieces), pieces.Named("StructureFrameIron").Key));
        bench.Built("StructureFrameIron", Large(0, 0, 0));
        bench.Start();
        bench.Ghost("ModStackFrame", Large(0, 1, 0));

        var thrown = Assert.Throws<InvalidOperationException>(() => bench.Pump());
        bench.Frame();

        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        Assert.True(bench.Service.Faulted);
        var refused = bench.Service.Check(bench.All());
        Assert.True(refused.TryGetError(out var why));
        Assert.Equal(ReasonCodes.ModuleMissing, why.Code);
    }

    [Fact]
    public void Verdict_changes_are_signalled_with_before_and_after()
    {
        var bench = new ValidationBench().Start();
        var changes = new List<VerdictChange>();
        bench.Service.VerdictsChanged += c => changes.AddRange(c);
        var ghost = bench.Ghost("StructureFrameIron", Large(0, 0, 0));
        bench.Pump();
        Assert.Contains(changes, c => c.Ghost == ghost && c.Before.IsNone && c.After.TryGet(out var f) && f is Finding.Passes);
        changes.Clear();

        bench.Rules.Register(new RuleDescriptor(Rule("no"), _ => new RuleVerdict.Fail(Why("no"))));
        bench.Pump();

        var change = Assert.Single(changes);
        Assert.True(change.Before.TryGet(out var before) && before is Finding.Passes);
        Assert.True(change.After.TryGet(out var after) && after is Finding.WillFail);
    }

    [Fact]
    public void A_wait_names_only_the_ghosts_the_viewer_may_see()
    {
        var bench = new ValidationBench().Start();
        var floor = bench.Ghost("ModStackFrame", Large(0, 0, 0));
        var upper = bench.Ghost("ModStackFrame", Large(0, 1, 0));
        bench.Policies.Install(new AccessPolicy((_, g) => g.Id != floor, (_, _) => true, _ => Result<Unit, Reason>.Ok(Unit.Value)));
        bench.Pump();

        var report = bench.Report(new Selector.Ids(ValueList.Of(upper)));

        var waits = Assert.IsType<Finding.WaitsFor>(Assert.Single(report.Findings));
        Assert.Empty(waits.Others);
    }

    private sealed class ThrowingFacts(ISupportFacts inner, PrefabKey piece) : ISupportFacts
    {
        public Option<SupportFacts> Of(PrefabKey asked) =>
            asked == piece ? throw new InvalidOperationException("Lattice bug") : inner.Of(asked);
    }
}
