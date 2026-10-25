using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Sync;

/// <summary>
/// The written forms of route toggles, branches, piece choices and splits: each new field written only when it is set,
/// so a request built as in contract 0.8 writes as it did.
/// </summary>
public sealed class RouteOptionFormTests
{
    private static readonly CommandId Id = new(new Guid(41, 3, 9, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    private static readonly KitKey Kit = PrefabHash.Kit("ModKitFancyCable");
    private static readonly Maker Maker = TestBoard.TestMaker;
    private static readonly PortEnd Port = new PortEnd.OnGhost(new GhostId(7), TestKitIds.Must(PortIndex.Create(2)));
    private const string Header = "lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\n";

    private static RouteRequest Route() =>
        new(Kit, new Cell(0, 0, 0), new Cell(100, 0, 0), new RouteToggles(true, false, true, false, true), Option<PlanId>.None, Maker);

    private static RunRequest Run() =>
        new(Kit, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(50, 0, 0), Option.Some(new PlanId(2)), Maker);

    private static string Written(Command command) =>
        CommandForm.Write(Id, command).Match(static text => text, static why => throw new InvalidOperationException(why.ToString()));

    public static IEnumerable<object[]> NewForms()
    {
        yield return [new Command.PlanRoute(Route() with { Toggles = Route().Toggles with { First = Option.Some(FirstLeg.Vertical) } })];
        yield return [new Command.PlanRoute(Route() with { Toggles = Route().Toggles with { First = Option.Some(FirstLeg.Level), HugFramesAndWalls = true } })];
        yield return [new Command.PlanRoute(Route() with { Lay = new LayOptions(true, true) })];
        yield return [new Command.PlanRoute(Route() with
        {
            Branches = ValueList.From(new[] { new RouteBranch(new Cell(20, 0, 30), Option<PortEnd>.None), new RouteBranch(new Cell(60, 0, -30), Option.Some(Port)) }),
        })];
        yield return [new Command.PlanRun(Run() with { Lay = new LayOptions(true, false) })];
        yield return [new Command.PlanRun(Run() with { Lay = new LayOptions(false, true) })];
        yield return [new Command.PlanSplit(new SplitTargets.Ghosts(new Selector.Ids(ValueList.Of(new GhostId(3)))), Option<PlanId>.None, Maker)];
        yield return [new Command.PlanSplit(new SplitTargets.Structures(new StructureSelector.Box(new Cell(0, 0, 0), new Cell(20, 20, 20))), Option.Some(new PlanId(5)), Maker)];
    }

    [Theory]
    [MemberData(nameof(NewForms))]
    public void Each_new_field_reads_back_as_written(Command command)
    {
        var read = CommandForm.Read(Written(command));

        Assert.True(read.TryGet(out var back), read.ToString());
        Assert.Equal(command, back.Command);
    }

    [Fact]
    public void A_zero_eight_run_and_route_write_as_they_did()
    {
        Assert.Equal(
            Header + "plan-run kit=1101555311 from=0,0,0 to=50,0,0 made=testkit/board-test into=2\n",
            Written(new Command.PlanRun(Run())));
        Assert.Equal(
            Header + "plan-route kit=1101555311 from=0,0,0 to=100,0,0 made=testkit/board-test frames=1 bends=0 doors=1 ports=0 no-bridge=1\n",
            Written(new Command.PlanRoute(Route())));
    }

    [Fact]
    public void The_new_fields_are_written_as_named_flags_cells_and_port_ends()
    {
        var route = new Command.PlanRoute(Route() with
        {
            Toggles = Route().Toggles with { First = Option.Some(FirstLeg.Vertical), HugFramesAndWalls = true },
            Lay = new LayOptions(true, true),
            Branches = ValueList.From(new[] { new RouteBranch(new Cell(20, 0, 30), Option<PortEnd>.None), new RouteBranch(new Cell(60, 0, -30), Option.Some(Port)) }),
        });

        Assert.Equal(
            Header + "plan-route kit=1101555311 from=0,0,0 to=100,0,0 made=testkit/board-test frames=1 bends=0 doors=1 ports=0 no-bridge=1"
                + " first=vertical hug=1 branch=20,0,30 branch=60,0,-30@7:2 singles=1 grade=1\n",
            Written(route));
    }

    [Fact]
    public void A_split_is_written_with_what_it_acts_on_and_its_selector()
    {
        var split = Written(new Command.PlanSplit(new SplitTargets.Ghosts(new Selector.Ids(ValueList.Of(new GhostId(3)))), Option<PlanId>.None, Maker));

        Assert.Contains("plan-split made=testkit/board-test of=ghosts which=#", split);
    }

    [Theory]
    [InlineData("first=sideways")]
    [InlineData("branch=1,2")]
    [InlineData("branch=0,0,0@7")]
    [InlineData("singles=yes")]
    public void A_new_field_that_does_not_read_is_refused(string field)
    {
        var read = CommandForm.Read(Header + $"plan-route kit=1101555311 from=0,0,0 to=100,0,0 made=testkit/board-test frames=1 bends=0 doors=1 ports=0 no-bridge=1 {field}\n");

        Assert.True(read.TryGetError(out var why));
        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
    }
}
