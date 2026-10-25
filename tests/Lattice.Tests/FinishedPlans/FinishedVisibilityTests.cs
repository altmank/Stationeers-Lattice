using System.IO;
using System.Linq;
using Lattice.Api;
using Lattice.Core.Board;
using Lattice.TestKit.Allocations;
using Lattice.Tests.Board;
using static Lattice.Tests.FinishedPlans.FinishedBench;

namespace Lattice.Tests.FinishedPlans;

public sealed class FinishedVisibilityTests
{
    private static readonly Actor Alice = TestBoard.Alice;
    private static readonly Actor Bob = TestBoard.Bob;

    /// <summary>A plan of one built wall, finished; made by Alice with the visibility given.</summary>
    private static PlanId Finished(FinishedBench b, string name, Visibility visibility, int cell)
    {
        var plan = b.NewPlan(name, visibility);
        b.Construct(b.Plan(plan, "ModPanel", Large(cell)));
        b.Finish(plan);
        return plan;
    }

    /// <summary>A policy that lets everyone see a plan open to everyone, and its owner see an owner-only one.</summary>
    private static AccessPolicy OwnersSeeTheirOwn(System.Func<bool>? hidesNothing = null) => new(
        (_, _) => true,
        (viewer, plan) => (hidesNothing?.Invoke() ?? false) || plan.Visibility is Visibility.Everyone || plan.Owner == viewer,
        _ => Result<Unit, Reason>.Ok(Unit.Value));

    /// <summary>Rewrites the index as one written before it kept plan records.</summary>
    private static void WithoutRecords(FinishedBench b)
    {
        var lines = File.ReadAllText(b.Folder.IndexPath).Split('\n').Select(static line =>
            line.IndexOf(" record ", System.StringComparison.Ordinal) is var at and >= 0 ? line.Substring(0, at) : line);
        File.WriteAllText(b.Folder.IndexPath, string.Join("\n", lines));
    }

    private static PlanId[] Listed(GhostBoard board, Actor viewer) =>
        new PlansView(board, viewer).Finished.Select(static f => f.Id).OrderBy(static id => id.Value).ToArray();

    [Fact]
    public void The_finished_list_holds_only_the_plans_the_viewer_may_see()
    {
        using var b = new FinishedBench();
        var hall = Finished(b, "Hall", new Visibility.Everyone(), 0);
        var den = Finished(b, "Den", new Visibility.OwnerOnly(), 1);
        b.Board.Policies.Install(OwnersSeeTheirOwn());

        Assert.Equal(new[] { hall, den }, Listed(b.Board, Alice));
        Assert.Equal(new[] { hall }, Listed(b.Board, Bob));
    }

    [Fact]
    public void Without_a_policy_every_finished_plan_is_listed_and_a_repeat_read_allocates_nothing()
    {
        using var b = new FinishedBench();
        var hall = Finished(b, "Hall", new Visibility.Everyone(), 0);
        var den = Finished(b, "Den", new Visibility.OwnerOnly(), 1);
        var bob = new PlansView(b.Board, Bob);

        Assert.Equal(new[] { hall, den }, Listed(b.Board, Bob));
        _ = bob.Finished;
        Allocations.AssertNone(() => _ = bob.Finished, "finished list");
    }

    [Fact]
    public void A_filtered_list_is_kept_until_the_policy_answers_change_and_a_repeat_read_allocates_nothing()
    {
        using var b = new FinishedBench();
        var hall = Finished(b, "Hall", new Visibility.Everyone(), 0);
        var den = Finished(b, "Den", new Visibility.OwnerOnly(), 1);
        var open = false;
        var handle = b.Board.Policies.Install(OwnersSeeTheirOwn(() => open)).Match(static h => h, static r => throw new Xunit.Sdk.XunitException(r.ToString()));
        var bob = new PlansView(b.Board, Bob);
        _ = bob.Finished;
        Allocations.AssertNone(() => _ = bob.Finished, "filtered finished list");

        open = true;
        Assert.Equal(new[] { hall }, bob.Finished.Select(static f => f.Id).ToArray());
        handle.Changed();

        Assert.Equal(new[] { hall, den }, bob.Finished.Select(static f => f.Id).OrderBy(static id => id.Value).ToArray());
    }

    [Fact]
    public void A_plan_finished_later_is_listed_by_the_same_rule()
    {
        using var b = new FinishedBench();
        b.Board.Policies.Install(OwnersSeeTheirOwn());
        var bob = new PlansView(b.Board, Bob);
        var hall = Finished(b, "Hall", new Visibility.Everyone(), 0);
        Assert.Equal(new[] { hall }, bob.Finished.Select(static f => f.Id).ToArray());

        Finished(b, "Den", new Visibility.OwnerOnly(), 1);

        Assert.Equal(new[] { hall }, bob.Finished.Select(static f => f.Id).ToArray());
    }

    [Fact]
    public void The_index_keeps_each_plans_record_so_a_loaded_world_filters_the_same()
    {
        using var b = new FinishedBench();
        var hall = Finished(b, "Hall", new Visibility.Everyone(), 0);
        var den = Finished(b, "Den", new Visibility.OwnerOnly(), 1);
        var save = b.Save();

        b.Reload(Option.Some(save));
        b.Board.Policies.Install(OwnersSeeTheirOwn());

        Assert.All(b.Folder.Finished(), static entry => Assert.True(entry.Record.IsSome));
        Assert.Equal(new Visibility.OwnerOnly(), b.Folder.Finished().Single(e => e.Plan.Id == den).Record.Match(static p => p.Visibility, static () => null!));
        Assert.Equal(new[] { hall }, Listed(b.Board, Bob));
        Assert.Equal(new[] { hall, den }, Listed(b.Board, Alice));
    }

    [Fact]
    public void An_index_written_without_records_gets_them_from_the_plan_files_at_load_and_is_rewritten()
    {
        using var b = new FinishedBench();
        var hall = Finished(b, "Hall", new Visibility.Everyone(), 0);
        var den = Finished(b, "Den", new Visibility.OwnerOnly(), 1);
        var save = b.Save();
        WithoutRecords(b);
        Assert.Equal(2, b.Folder.Finished().Count);
        Assert.All(b.Folder.Finished(), static entry => Assert.True(entry.Record.IsNone));

        b.Reload(Option.Some(save));
        b.Board.Policies.Install(OwnersSeeTheirOwn());

        Assert.Equal(new[] { hall }, Listed(b.Board, Bob));
        Assert.Equal(new[] { hall, den }, Listed(b.Board, Alice));
        Assert.All(b.Folder.Finished(), static entry => Assert.True(entry.Record.IsSome));
    }

    [Fact]
    public void A_listed_plan_with_no_record_is_hidden_while_a_policy_is_installed()
    {
        using var b = new FinishedBench();
        var hall = Finished(b, "Hall", new Visibility.Everyone(), 0);
        var den = Finished(b, "Den", new Visibility.Everyone(), 1);
        var save = b.Save();
        WithoutRecords(b);
        File.Delete(b.Folder.ArchivedPath(den));

        b.Reload(Option.Some(save));
        Assert.Equal(new[] { hall, den }, Listed(b.Board, Bob));
        b.Board.Policies.Install(OwnersSeeTheirOwn());

        Assert.Equal(new[] { hall }, Listed(b.Board, Bob));
    }
}
