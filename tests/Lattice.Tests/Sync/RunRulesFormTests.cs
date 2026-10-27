using System;
using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.TestKit.Catalogue;
using TestBoard = Lattice.Tests.Board.TestBoard;

namespace Lattice.Tests.Sync;

/// <summary>
/// The written forms of <see cref="RouteToggles.KeepOffLines"/> (<c>lines=1</c> on a route) and
/// <see cref="RunRequest.Rules"/> (<c>rules=</c> on a run, the toggles set by name): written only when set, so a request
/// without them writes as it did, and a reader that does not know the field refuses the command rather than drop it.
/// </summary>
public sealed class RunRulesFormTests
{
    private static readonly CommandId Id = new(new Guid(41, 3, 9, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
    private static readonly KitKey Kit = PrefabHash.Kit("ModKitFancyCable");
    private const string Header = "lattice-command 1 id=KQAAAAMACQABAgMEBQYHCA\n";
    private const string RunText = "plan-run kit=1101555311 from=0,0,0 to=50,0,0 made=testkit/board-test into=2";
    private const string RouteText = "plan-route kit=1101555311 from=0,0,0 to=100,0,0 made=testkit/board-test frames=1 bends=0 doors=1 ports=0 no-bridge=1";

    private static readonly RouteToggles Off = new(false, false, false, false, false);

    private static RouteRequest Route() =>
        new(Kit, new Cell(0, 0, 0), new Cell(100, 0, 0), new RouteToggles(true, false, true, false, true), Option<PlanId>.None, TestBoard.TestMaker);

    private static RunRequest Run() =>
        new(Kit, new Cell(0, 0, 0), ValueList<Cell>.Empty, new Cell(50, 0, 0), Option.Some(new PlanId(2)), TestBoard.TestMaker);

    private static string Written(Command command) =>
        CommandForm.Write(Id, command).Match(static text => text, static why => throw new InvalidOperationException(why.ToString()));

    public static IEnumerable<object[]> NewForms()
    {
        yield return [new Command.PlanRoute(Route() with { Toggles = Route().Toggles with { KeepOffLines = true } })];
        yield return [new Command.PlanRun(Run() with { Rules = Option.Some(Off) })];
        yield return [new Command.PlanRun(Run() with { Rules = Option.Some(Off with { KeepOffLines = true }) })];
        yield return [new Command.PlanRun(Run() with { Rules = Option.Some(new RouteToggles(false, false, true, true, true) { KeepOffLines = true }) })];
        yield return [new Command.PlanRun(Run() with
        {
            Rules = Option.Some(new RouteToggles(true, true, false, false, false) { HugFramesAndWalls = true, First = Option.Some(FirstLeg.Level) }),
        })];
        yield return [new Command.PlanRun(Run() with { Rules = Option.Some(Off with { First = Option.Some(FirstLeg.Vertical) }) })];
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
    public void The_new_fields_are_written_by_name()
    {
        Assert.Equal(
            Header + RouteText + " lines=1\n",
            Written(new Command.PlanRoute(Route() with { Toggles = Route().Toggles with { KeepOffLines = true } })));
        Assert.Equal(
            Header + "plan-run kit=1101555311 from=0,0,0 to=50,0,0 made=testkit/board-test rules=doors,ports,no-bridge,lines into=2\n",
            Written(new Command.PlanRun(Run() with { Rules = Option.Some(new RouteToggles(false, false, true, true, true) { KeepOffLines = true }) })));
        Assert.Contains(" rules=none ", Written(new Command.PlanRun(Run() with { Rules = Option.Some(Off) })), StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_and_route_without_them_write_as_they_did()
    {
        Assert.Equal(Header + RunText + "\n", Written(new Command.PlanRun(Run())));
        Assert.Equal(Header + RouteText + "\n", Written(new Command.PlanRoute(Route())));
    }

    [Fact]
    public void A_run_read_without_rules_judges_nothing()
    {
        var read = CommandForm.Read(Header + RunText + "\n");

        Assert.True(read.TryGet(out var back), read.ToString());
        Assert.True(Assert.IsType<Command.PlanRun>(back.Command).Request.Rules.IsNone);
    }

    [Theory]
    [InlineData("rules=wide")]
    [InlineData("rules=lines,")]
    [InlineData("rules=")]
    public void Rules_that_do_not_read_are_refused(string field)
    {
        var read = CommandForm.Read(Header + RunText + $" {field}\n");

        Assert.True(read.TryGetError(out var why));
        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
    }

    [Theory]
    [InlineData("plan-run", RunText + " later=1")]
    [InlineData("plan-route", RouteText + " later=1")]
    public void A_field_the_reader_does_not_know_refuses_the_command_rather_than_drop_it(string kind, string text)
    {
        var read = CommandForm.Read(Header + text + "\n");

        Assert.True(read.TryGetError(out var why));
        Assert.Equal(ReasonCodes.InvalidValue, why.Code);
        Assert.Contains($"unknown field 'later' on {kind}", why.Facts["problem"], StringComparison.Ordinal);
    }
}
