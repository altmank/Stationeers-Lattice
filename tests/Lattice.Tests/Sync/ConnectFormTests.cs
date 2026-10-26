using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.TestKit;
using Lattice.TestKit.Catalogue;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Sync;

/// <summary>
/// The written forms of a port end on a built structure and of a route's via cells. Each is written so a reader that
/// knows neither refuses the command rather than reads it as something else or drops the field.
/// </summary>
public sealed class ConnectFormTests
{
    private static readonly CommandId Id = new(new Guid(41, 3, 9, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    private static readonly KitKey Kit = PrefabHash.Kit("ModKitFancyCable");
    private static readonly Maker Maker = TestBoard.TestMaker;
    private static readonly StructureRef Device = new(new ReferenceId(4021), new PrefabKey(-118), new Cell(-5, 10, 15));
    private static readonly PortEnd Built = new PortEnd.OnStructure(Device, TestKitIds.Must(PortIndex.Create(3)));
    private const string Header = "lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\n";

    private static RouteRequest Route() =>
        new(Kit, new Cell(0, 0, 0), new Cell(100, 0, 0), new RouteToggles(true, false, true, false, true), Option<PlanId>.None, Maker);

    private static RunRequest Run() =>
        new(Kit, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(50, 0, 0), Option.Some(new PlanId(2)), Maker);

    private static string Written(Command command) =>
        CommandForm.Write(Id, command).Match(static text => text, static why => throw new InvalidOperationException(why.ToString()));

    public static IEnumerable<object[]> NewForms()
    {
        yield return [new Command.PlanRun(Run() with { FromPort = Option.Some(Built) })];
        yield return [new Command.PlanRun(Run() with { ToPort = Option.Some(Built) })];
        yield return [new Command.PlanRoute(Route() with { FromPort = Option.Some(Built), ToPort = Option.Some<PortEnd>(new PortEnd.OnGhost(new GhostId(7), TestKitIds.Must(PortIndex.Create(0)))) })];
        yield return [new Command.PlanRoute(Route() with { Branches = ValueList.Of(new RouteBranch(new Cell(20, 0, 30), Option.Some(Built))) })];
        yield return [new Command.PlanRoute(Route() with { Via = ValueList.From(new[] { new Cell(40, 0, 20), new Cell(60, -10, 20) }) })];
    }

    [Theory]
    [MemberData(nameof(NewForms))]
    public void Each_reads_back_as_written(Command command)
    {
        var read = CommandForm.Read(Written(command));

        Assert.True(read.TryGet(out var back), read.ToString());
        Assert.Equal(command, back.Command);
    }

    [Fact]
    public void A_built_port_end_and_via_cells_are_written_as_named_fields()
    {
        var route = new Command.PlanRoute(Route() with { ToPort = Option.Some(Built), Via = ValueList.From(new[] { new Cell(40, 0, 20), new Cell(60, -10, 20) }) });

        Assert.Equal(
            Header + "plan-route kit=1101555311 from=0,0,0 to=100,0,0 made=testkit/board-test frames=1 bends=0 doors=1 ports=0 no-bridge=1"
                + " to-port=s4021:-118@-5,10,15:3 via=40,0,20 via=60,-10,20\n",
            Written(route));
    }

    [Fact]
    public void A_built_port_end_cannot_be_read_as_a_ghost_port_end()
    {
        var text = Written(new Command.PlanRun(Run() with { ToPort = Option.Some(Built) }));
        var field = text.Substring(text.IndexOf("to-port=", StringComparison.Ordinal) + "to-port=".Length).Split(' ', '\n')[0];

        Assert.False(long.TryParse(field.Substring(0, field.IndexOf(':')), out _));
    }

    [Fact]
    public void A_field_the_reader_does_not_know_refuses_the_route()
    {
        var read = CommandForm.Read(Header + "plan-route kit=1101555311 from=0,0,0 to=100,0,0 made=testkit/board-test frames=1 bends=0 doors=1 ports=0 no-bridge=1 pinned=40,0,20\n");

        Assert.True(read.TryGetError(out _));
    }

    [Theory]
    [InlineData("via=1,2")]
    [InlineData("to-port=s4021:-118@-5,10,15")]
    [InlineData("to-port=s4021@-5,10,15:3")]
    [InlineData("to-port=sx:-118@-5,10,15:3")]
    public void A_new_field_that_does_not_read_is_refused(string field)
    {
        var read = CommandForm.Read(Header + $"plan-route kit=1101555311 from=0,0,0 to=100,0,0 made=testkit/board-test frames=1 bends=0 doors=1 ports=0 no-bridge=1 {field}\n");

        Assert.True(read.TryGetError(out var why));
        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
    }
}
