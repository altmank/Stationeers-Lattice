using System;
using System.Collections.Generic;
using Lattice.Api;

namespace Lattice.Consumer.Tests;

/// <summary>
/// A consumer's flows written against the contract alone, run on every system that derives this: the stub here, and
/// Lattice's real system over fakes in Lattice.Tests (the file is compiled into both). A derived class gives a host
/// world, a player, drafts of a piece the world knows, and a way to run frames until the world's answers settle.
/// </summary>
public abstract class ConsumerFlows
{
    private const int MostFrames = 32;

    private protected abstract IGhostSystem System { get; }

    private protected abstract Actor Player { get; }

    /// <summary>A draft of a ghost at a free spot; a different spot for each <paramref name="spot"/>.</summary>
    private protected abstract GhostDraft Draft(int spot);

    /// <summary>Runs frames until the world has nothing left to work out.</summary>
    private protected abstract void Frames();

    /// <summary>A placed ghost: the stub plays the host's part here; the real system needs nothing.</summary>
    private protected virtual void Placed(Ghost ghost)
    {
    }

    private static Ok Must<Ok>(Result<Ok, Reason> result) where Ok : notnull =>
        result.Match(static value => value, static why => throw new Xunit.Sdk.XunitException($"refused: {why}"));

    private Ghost Place(int spot)
    {
        var submission = System.Commands.Submit(CommandId.New(), new Command.Place(ValueList.Of(Draft(spot))), Player);
        var answered = Assert.IsType<Submission.Answered>(submission);
        var ghost = Assert.Single(Assert.IsType<Outcome.Applied>(answered.Outcome).Changes.Added);
        Placed(ghost);
        return ghost;
    }

    private T Settled<T>(Func<Answer<T>> ask) where T : notnull
    {
        for (var i = 0; i < MostFrames; i++)
        {
            if (ask() is Answer<T>.Ready ready)
                return ready.Value;
            Frames();
        }

        throw new Xunit.Sdk.XunitException("the answer stayed pending");
    }

    [Fact]
    public void A_placed_ghosts_ports_are_read_from_its_placement_and_each_names_a_run_end_at_its_joining_cell()
    {
        var ghost = Place(0);
        var placement = ghost.Intent switch
        {
            Intent.Build build => build.Target,
            Intent.Swap swap => swap.New,
            _ => throw new Xunit.Sdk.XunitException("no new piece"),
        };

        foreach (var port in Must(System.Catalogue.PortsAt(placement)))
        {
            var end = new PortEnd.OnGhost(ghost.Id, port.Index);
            var run = new RunRequest(default, port.Joins, ValueList<Cell>.Empty, port.Joins, Option<PlanId>.None, ghost.MadeBy)
            {
                FromPort = Option.Some<PortEnd>(end),
            };
            var route = new RouteRequest(default, port.Joins, port.Joins, new RouteToggles(false, false, false, true, false), Option<PlanId>.None, ghost.MadeBy)
            {
                ToPort = Option.Some<PortEnd>(end),
            };

            Assert.Equal(port.Joins, run.From);
            Assert.Equal(route.ToPort, run.FromPort);
            Assert.NotEmpty(port.Kinds);
            Assert.Equal(port.Joins, port.From.Neighbour(port.Toward, PieceGrid.Small));
        }
    }

    /// <summary>What a consumer shows for each port end a run report names.</summary>
    private protected static IEnumerable<string> PortEndLines(RunReport report)
    {
        foreach (var join in report.PortEnds)
            yield return join.End switch
            {
                PortEnd.OnGhost onGhost => $"ghost {onGhost.Ghost.Value} port {onGhost.Port.Value} ({join.Port.GameName}): " +
                                           (join.JoinedBy.TryGet(out var by) ? $"joined at {by.At}" : "open"),
                _ => "an end this consumer does not know",
            };
    }

    [Fact]
    public void A_placed_ghost_is_answered_at_once_and_raised_as_a_change()
    {
        var changed = new List<GhostId>();
        Action<ChangeSet> onChanged = changes =>
        {
            foreach (var added in changes.Added)
                changed.Add(added.Id);
        };
        System.Events.Changed += onChanged;
        try
        {
            var ghost = Place(0);
            Frames();

            Assert.Equal(new[] { ghost.Id }, changed);
        }
        finally
        {
            System.Events.Changed -= onChanged;
        }
    }

    [Fact]
    public void A_builder_claims_what_passes_reads_its_steps_and_releases()
    {
        var ghost = Place(0);
        Frames();
        var report = Settled(() => Must(System.Validation.Check(new Selector.Ids(ValueList.Of(ghost.Id)))));
        var builder = Must(System.Executors.Register(new ExecutorInfo(
            Must(ExecutorId.Create(Must(ModId.Create("builder")), "drone-1")), Must(DisplayName.Create("Drone 1")))));
        var claims = new List<Claim>();
        foreach (var finding in report.Findings)
            if (finding is Finding.Passes && builder.Claim(finding.Ghost, Must(Lease.Create(30))).TryGet(out var claim))
                claims.Add(claim);

        var claimed = Assert.Single(claims);
        var plan = Must(System.Steps.For(claimed.Ghost));
        Assert.NotEmpty(plan.Steps);
        Assert.True(builder.Report(claimed.Ghost, new ExecutorStatus.Working(1, plan.Steps.Count)).TryGet(out _));
        builder.Release(claimed);
        builder.Dispose();
    }

    [Fact]
    public void A_preview_shows_what_a_place_would_add()
    {
        var command = new Command.Place(ValueList.Of(Draft(1)));

        var preview = Settled(() => System.Preview.Of(command, Player));

        Assert.Single(preview.Match(static p => p.WouldChange.Added, static why => throw new Xunit.Sdk.XunitException($"refused: {why[0].Why}")));
    }

    [Fact]
    public void A_mods_rule_registers_and_its_slices_run_until_done()
    {
        var rule = Must(System.Registries.Register(new RuleDescriptor(
            Must(RuleId.Create(Must(ModId.Create("addon")), "no-rule")), static _ => new RuleVerdict.Pass())));
        var steps = 0;
        var work = Must(System.Slices.Schedule(new SlicedWorkDescriptor(
            Must(SliceId.Create(Must(ModId.Create("planner")), "overlay")), () => ++steps < 3)));

        for (var i = 0; i < MostFrames && steps < 3; i++)
            Frames();

        Assert.Equal(3, steps);
        work.Dispose();
        rule.Dispose();
    }

    [Fact]
    public void A_picker_lists_a_kits_options_then_finds_a_built_pieces_kit()
    {
        Assert.True(System.Version.Major > 0 || System.Version.Minor >= 3);
        var catalogue = System.Catalogue;
        var piece = ((Intent.Build)Draft(0).Intent).Target.Piece;

        var place = Must(catalogue.KitOf(piece));
        var kit = Must(catalogue.Kit(place.Kit));
        var names = new List<string>(kit.Options.Count);
        foreach (var option in kit.Options)
            names.Add(Must(catalogue.Piece(option)).Name);

        Assert.Equal(piece, kit.Options[place.Option.Index]);
        Assert.Equal(piece, Must(catalogue.PieceOf(place.Kit, place.Option)));
        Assert.Equal(kit.Options.Count, names.Count);
        Assert.Contains(place, Must(catalogue.Piece(piece)).Kits);
        Assert.Contains(kit, catalogue.Kits);
        Assert.Contains(catalogue.Pieces, entry => entry.Key == piece && entry.Grid is PieceGrid.Small or PieceGrid.Large && entry.PrefabName.Length > 0);
        Assert.NotEqual(default, Must(catalogue.FamilyOf(piece)));
        var chain = Must(catalogue.FamiliesOf(piece));
        Assert.Equal(Must(catalogue.FamilyOf(piece)), chain[chain.Count - 1]);
        Assert.Equal(Option.Some(PieceFamilies.Wall), catalogue.ParentOf(PieceFamilies.Window));
        Assert.False(catalogue.ParentOf(PieceFamilies.Wall).IsSome);
        Assert.Equal(ReasonCodes.UnknownPiece, Error(catalogue.Piece(default)).Code);
    }

    private static Reason Error<Ok>(Result<Ok, Reason> result) where Ok : notnull =>
        result.Match(static ok => throw new Xunit.Sdk.XunitException($"expected a refusal, got {ok}"), static why => why);

    [Fact]
    public void A_consumer_checks_the_version_kinds_and_modules_itself()
    {
        Assert.Equal(ApiVersion.ContractMajor, System.Version.Major);
        Assert.True(System.Version.Minor >= ApiVersion.ContractMinor);
        Assert.True(System.Has(IntentKind.Build));
        Assert.False(System.Has((IntentKind)99));
        foreach (var module in System.Modules)
            if (module.State is ModuleState.Off off)
                Assert.Equal(ReasonCodes.ModuleMissing, off.Why.Code);
    }
}
