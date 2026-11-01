using System.Collections.Generic;
using Lattice.Api;
using Lattice.Core.Sync;
using Lattice.Tests.Board;
using Lattice.Tests.Executors;
using Lattice.TestKit;

namespace Lattice.Tests.Sync;

/// <summary>
/// Build modes on every seat: a client's <see cref="Command.SetBuildMode"/> goes to the host, reaches the builder's
/// handler as the client's player, and its answer comes back on the client's ticket; what the builder says about its
/// mode, who may set it, and its batch's size read the same on a client, a late joiner included.
/// </summary>
public sealed class BuildModeSyncTests
{
    private static readonly Reason Busy = new(
        TestKitIds.Must(ReasonCode.Create(TestKitIds.Mod, "busy")),
        ValueMap.Of("job", "hall"),
        Option.Some("The dock is busy"),
        Builders.Dock);

    private static ExecutorId Dock => Builders.Id("dock");

    private static Builder ReadOn(ExecutorSyncBench.Seat seat, ExecutorId id) =>
        seat.Executors.Find(id).Reduce(() => throw new Xunit.Sdk.XunitException($"{id} is not on the client"));

    private static Outcome Ask(ExecutorSyncBench bench, ExecutorSyncBench.Seat client, BuildMode mode)
    {
        var id = new CommandId(System.Guid.NewGuid());
        Assert.IsType<Submission.Sent>(client.Sync.Submit(id, new Command.SetBuildMode(Dock, mode), new Actor.Player(new ClientId(1))));
        bench.Settle();
        var (ticket, outcome) = Assert.Single(client.Answers);
        Assert.Equal(id, ticket.Command);
        client.Answers.Clear();
        return outcome;
    }

    [Fact]
    public void A_client_s_request_reaches_the_handler_as_its_player_and_the_answer_comes_back_on_its_ticket()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var dock = bench.Register("dock");
        var asked = new List<(Actor, BuildMode)>();
        var answer = Result<Unit, Reason>.Ok(Unit.Value);
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.OwnerAndHost(), (actor, mode) =>
        {
            asked.Add((actor, mode));
            return answer;
        }));
        bench.Settle();

        var accepted = Ask(bench, client, new BuildMode.Batch());
        answer = Result<Unit, Reason>.Error(Busy);
        var refused = Ask(bench, client, new BuildMode.Off());

        Assert.IsType<Outcome.Accepted>(accepted);
        Assert.Equal(Busy, Assert.Single(Assert.IsType<Outcome.Refused>(refused).Why).Why);
        Assert.Equal(new (Actor, BuildMode)[]
        {
            (new Actor.Player(new ClientId(1)), new BuildMode.Batch()),
            (new Actor.Player(new ClientId(1)), new BuildMode.Off()),
        }, asked);
    }

    [Fact]
    public void A_client_hears_not_found_and_not_supported_from_the_host()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);

        var missing = Ask(bench, client, new BuildMode.On());
        var dock = bench.Register("dock");
        bench.Settle();
        var none = Ask(bench, client, new BuildMode.On());
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) => Result<Unit, Reason>.Ok(Unit.Value)));
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.None(), null));
        var takenAway = Ask(bench, client, new BuildMode.On());

        Assert.Equal(ReasonCodes.NotFound, Assert.Single(Assert.IsType<Outcome.Refused>(missing).Why).Why.Code);
        Assert.Equal(ReasonCodes.NotSupported, Assert.Single(Assert.IsType<Outcome.Refused>(none).Why).Why.Code);
        Assert.Equal(ReasonCodes.NotSupported, Assert.Single(Assert.IsType<Outcome.Refused>(takenAway).Why).Why.Code);
    }

    [Fact]
    public void An_accepted_request_moves_no_revision_on_the_client()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var dock = bench.Register("dock");
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) => Result<Unit, Reason>.Ok(Unit.Value)));
        bench.Settle();
        var revision = client.T.Board.Revision;
        client.Log.Clear();

        Assert.IsType<Outcome.Accepted>(Ask(bench, client, new BuildMode.On()));

        Assert.Equal(revision, client.T.Board.Revision);
        Assert.DoesNotContain(client.Log, line => line.StartsWith("changed"));
    }

    [Fact]
    public void What_the_builder_says_reads_the_same_on_a_client_and_changes_raise_one_builder_changed()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var dock = bench.Register("dock");
        var a = bench.Ghost();
        var b = bench.Ghost();
        bench.Settle();
        var told = new List<ExecutorId>();
        client.T.Events.BuilderChanged += told.Add;

        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.OwnerAndHost(), (_, _) => Result<Unit, Reason>.Ok(Unit.Value)));
        TestKitIds.Must(dock.SayMode(new BuildMode.Batch(), ValueList.Of(new SkippedGhost(new GhostId(42), Busy))));
        TestKitIds.Must(dock.SetBatch(ValueSet.From(new[] { a, b })));
        bench.Settle();

        var seen = ReadOn(client, Dock);
        Assert.Equal(bench.Executors.Find(Dock), client.Executors.Find(Dock));
        Assert.IsType<BuildModeAccess.OwnerAndHost>(seen.ControlledBy);
        Assert.Equal(Option.Some<BuildMode>(new BuildMode.Batch()), seen.Mode);
        Assert.Equal(2, seen.BatchLeft);
        var skipped = Assert.Single(seen.BatchSkipped);
        Assert.Equal(Busy, skipped.Why);
        Assert.Equal(Option.Some("The dock is busy"), skipped.Why.GameMessage);
        Assert.Equal(new[] { Dock }, told);

        TestKitIds.Must(dock.SayMode(new BuildMode.Batch(), ValueList.Of(new SkippedGhost(new GhostId(42), Busy))));
        bench.Settle();
        Assert.Single(told);
    }

    [Fact]
    public void Batch_left_follows_the_board_on_the_client()
    {
        var bench = new ExecutorSyncBench();
        var client = bench.Join(1);
        var dock = bench.Register("dock");
        var a = bench.Ghost();
        var b = bench.Ghost();
        TestKitIds.Must(dock.SetBatch(ValueSet.From(new[] { a, b })));
        bench.Settle();

        bench.Remove(a);
        bench.Settle();

        Assert.Equal(1, ReadOn(client, Dock).BatchLeft);
    }

    [Fact]
    public void A_client_joining_later_reads_the_mode_values_and_a_disposed_builder_is_gone()
    {
        var bench = new ExecutorSyncBench();
        var dock = bench.Register("dock");
        TestKitIds.Must(dock.AcceptModes(new BuildModeAccess.Anyone(), (_, _) => Result<Unit, Reason>.Ok(Unit.Value)));
        TestKitIds.Must(dock.SayMode(new BuildMode.On(), ValueList<SkippedGhost>.Empty));
        TestKitIds.Must(dock.SetBatch(ValueSet.Of(bench.Ghost())));
        bench.Settle();

        var late = bench.Join(2);
        bench.Settle();

        Assert.Equal(bench.Executors.Find(Dock), late.Executors.Find(Dock));
        Assert.IsType<BuildModeAccess.Anyone>(ReadOn(late, Dock).ControlledBy);

        dock.Dispose();
        bench.Settle();
        Assert.True(late.Executors.Find(Dock).IsNone);
    }

    [Fact]
    public void The_mode_command_and_the_accepted_answer_are_written_and_read_back()
    {
        foreach (var mode in new BuildMode[] { new BuildMode.On(), new BuildMode.Off(), new BuildMode.Batch() })
        {
            var id = new CommandId(System.Guid.NewGuid());
            Command command = new Command.SetBuildMode(Dock, mode);

            var read = TestKitIds.Must(CommandForm.Read(TestKitIds.Must(CommandForm.Write(id, command))));

            Assert.Equal((id, command), read);
        }

        var answerId = new CommandId(System.Guid.NewGuid());
        var answer = TestKitIds.Must(AnswerForm.Read(AnswerForm.Write(answerId, new Answer.Accepted())));
        Assert.Equal(answerId, answer.Id);
        Assert.IsType<Answer.Accepted>(answer.Answer);
    }
}
