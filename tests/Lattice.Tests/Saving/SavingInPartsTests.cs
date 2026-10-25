using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Saving;
using Lattice.Core.Slices;
using Lattice.Core.Written;

namespace Lattice.Tests.Saving;

/// <summary>
/// A very large change set (saving runs in a time slice per frame) is only queued when it
/// is recorded; the saving slice works it into its line a bounded number of operations a step, and the file ends up
/// exactly as the line written in one go. Every read of the plans' lines and every save answers as if the set had been
/// recorded at once.
/// </summary>
public sealed class SavingInPartsTests
{
    private const int Ghosts = 2000;

    [Fact]
    public void A_large_change_set_is_worked_into_its_line_across_steps_and_the_file_matches_the_line_written_at_once()
    {
        using var world = new SavedWorld();
        var set = PlaceMany(world);
        var plan = set.Added[0].Plan;
        Assert.False(File.Exists(world.Folder.PlanPath(plan)));

        var steps = 0;
        while (world.Saving.Step() == StepOutcome.More)
            steps++;

        Assert.True(steps >= 2 * Ghosts / PlanSaving.OpsPerStep, $"{steps} steps: the set is split into its plan part and written a few changes a step");
        var line = ChangeLine.For(new LineNumber(1), Option<LineNumber>.None, set, ChangeLines.Of(set).Single().Ops);
        Assert.EndsWith(PlanFileForm.Append(line), File.ReadAllText(world.Folder.PlanPath(plan)));
        Assert.Equal(0, world.Saving.Pending);
    }

    [Fact]
    public void Reads_and_a_save_answer_as_if_the_queued_set_had_been_recorded_at_once()
    {
        using var world = new SavedWorld();
        var set = PlaceMany(world);
        var plan = set.Added[0].Plan;
        Assert.Equal(1, world.Saving.Pending);

        Assert.Equal(1, world.Saving.Heads[plan]);
        Assert.Equal(0, global::Lattice.TestKit.TestKitIds.Must(world.Saving.AheadOfSave(plan)));

        var save = world.Save("monday");
        Assert.Equal(0, world.Saving.Pending);
        var report = world.Load(Option.Some(save));
        Assert.Empty(report.Problems);
        Assert.Equal(Ghosts, set.Added.Count(g => world.Ghosts.Contains(g.Id)));
    }

    [Fact]
    public void A_change_set_split_one_change_a_step_gives_the_parts_it_gives_split_at_once()
    {
        var ghosts = Lattice.Tests.Written.Samples.EveryGhost();
        var set = new ChangeSet(
            new ChangeSetId(5), Lattice.Tests.Written.Samples.Command, Lattice.Tests.Written.Samples.Player, new Revision(9), Lattice.Tests.Written.Samples.Monday,
            ValueList.Of(ghosts[0] with { Plan = new PlanId(7) }),
            ValueList.Of(new GhostChange(ghosts[1], ghosts[1] with { Plan = new PlanId(4) })),
            ValueList.Of(new GhostRemoval(ghosts[2], new RemovalCause.Removed())),
            ValueList.Of(Lattice.Tests.Written.Samples.Plan(7)),
            ValueList<PlanChange>.Empty,
            ValueList.Of(new PlanId(4)));

        var split = new ChangeSplit(set);
        var steps = 0;
        while (!split.Done)
        {
            Assert.Equal(1, split.Step(1));
            steps++;
        }

        Assert.Equal(5, steps);
        Assert.Equal(ChangeLines.Of(set), split.Parts);
    }

    private static ChangeSet PlaceMany(SavedWorld world)
    {
        var drafts = Enumerable.Range(0, Ghosts)
            .Select(i => world.Board.Draft("StructureFrame", new Cell(10 + (20 * i), 10, 10)))
            .ToList();
        return world.Apply(new Command.Place(ValueList.From(drafts)));
    }
}
